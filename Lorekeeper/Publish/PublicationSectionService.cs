using System.Security.Cryptography;
using System.Text.Json;
using Lorekeeper.Authoring;
using Lorekeeper.Composition;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Lorekeeper.VersionHistory.Services;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Publish;

public sealed record PublicationSectionTarget(Guid ProjectId, Guid? EditionId = null);

public sealed record PublicationSectionView(
    Guid Id,
    Guid? CoreSectionId,
    Guid? EditionId,
    string Title,
    PublicationSectionKind Kind,
    PublicationSectionSystemRole SystemRole,
    PublicationSectionAnchor Anchor,
    PublishOutlineTargetKind? TargetKind,
    Guid? TargetId,
    string TargetTitle,
    PublicationSectionInclusionMode InclusionMode,
    PublicationSectionStartSide StartSide,
    bool IsIncluded,
    bool IsInherited,
    bool HasOrderOverride,
    int LocalOrder,
    ManuscriptDocument Manuscript,
    long Revision,
    int DesignedPageCount,
    int FigureCount);

public sealed record PublicationSectionInput(
    Guid? Id,
    string Title,
    PublicationSectionKind Kind,
    PublicationSectionAnchor Anchor,
    PublishOutlineTargetKind? TargetKind,
    Guid? TargetId,
    PublicationSectionInclusionMode InclusionMode,
    PublicationSectionStartSide StartSide,
    string ManuscriptJson,
    long? ExpectedRevision = null);

public sealed record PublicationSectionDesignedPageResult(
    PublicationSectionView Section,
    DesignedPageView Page,
    string PlacementId);

public sealed record PublicationSectionHistoryResult(
    PublicationSectionView Section,
    AuthoringHistoryState History,
    string ActionLabel,
    string SelectionJson);

public interface IPublicationSectionService
{
    Task EnsureSystemSectionsAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PublicationSectionView>> ListAsync(PublicationSectionTarget target, CancellationToken cancellationToken = default);
    Task<PublicationSectionView> GetAsync(PublicationSectionTarget target, Guid sectionId, CancellationToken cancellationToken = default);
    Task<PublicationSectionView> UpsertAsync(PublicationSectionTarget target, PublicationSectionInput input, CancellationToken cancellationToken = default);
    Task<PublicationSectionView> SetInclusionAsync(PublicationSectionTarget target, Guid sectionId, PublicationSectionInclusionMode inclusionMode, long expectedRevision, CancellationToken cancellationToken = default);
    Task<PublicationSectionView> PatchManuscriptAsync(PublicationSectionTarget target, Guid sectionId, long expectedRevision, IReadOnlyList<ManuscriptOperationInput> operations, CancellationToken cancellationToken = default);
    Task<PublicationSectionView> ApplyAuthoringOperationsAsync(PublicationSectionTarget target, Guid sectionId, long expectedRevision, IReadOnlyList<ManuscriptOperation> operations, CancellationToken cancellationToken = default);
    Task RefreshAuthoringDerivedStateAsync(PublicationSectionTarget target, Guid sectionId, CancellationToken cancellationToken = default);
    Task<PublicationSectionView> CustomizeAsync(Guid projectId, Guid editionId, Guid coreSectionId, CancellationToken cancellationToken = default);
    Task<PublicationSectionView> EnsureSystemDesignedPageAsync(PublicationSectionTarget target, Guid sectionId, CancellationToken cancellationToken = default);
    Task ResetAsync(Guid projectId, Guid editionId, Guid sectionId, CancellationToken cancellationToken = default);
    Task DeleteAsync(PublicationSectionTarget target, Guid sectionId, CancellationToken cancellationToken = default);
    Task ReorderWithinAnchorAsync(PublicationSectionTarget target, IReadOnlyList<Guid> orderedSectionIds, CancellationToken cancellationToken = default);
    Task<PublicationSectionDesignedPageResult> CreateDesignedPageAsync(PublicationSectionTarget target, Guid sectionId, int blockIndex, string name, DesignedPageLayoutMode layoutMode, long expectedRevision, CancellationToken cancellationToken = default);
    Task<string> ResolveBoundFieldAsync(PublicationSectionTarget target, PublicationBoundField field, CancellationToken cancellationToken = default);
}

public sealed class PublicationSectionService(
    IAppDatabaseOperationFactory database,
    IPublicationBookService books,
    IPublicationEffectiveConfigurationResolver effectiveConfigurations,
    IManuscriptStyleService manuscriptStyles,
    IDesignedPageService designedPages,
    IAuthoringDeltaHistoryRuntime deltaHistory,
    IAuthoringMutationContextAccessor authoringMutationContext,
    IAuthoringGenerationService authoringGenerations,
    ProjectVersionHistoryUiEvents historyEvents) : IPublicationSectionService
{
    public async Task EnsureSystemSectionsAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        _ = await books.GetOrCreateAsync(projectId, cancellationToken);
        var existing = await db.PublicationSections.AsNoTracking()
            .Where(item => item.ProjectId == projectId && item.EditionId == null && item.SystemRole != PublicationSectionSystemRole.None)
            .Select(item => item.SystemRole)
            .ToListAsync(cancellationToken);
        if (existing.Count == 3)
            return;

        var roles = (await db.PublicationSections.AsNoTracking()
            .Where(item => item.ProjectId == projectId && item.EditionId == null && item.SystemRole != PublicationSectionSystemRole.None)
            .Select(item => item.SystemRole)
            .ToListAsync(cancellationToken)).ToHashSet();
        var now = DateTime.UtcNow;
        var added = false;
        foreach (var (role, kind, title, order, startSide) in SystemSectionDefinitions)
        {
            if (roles.Contains(role))
                continue;
            added = true;
            var id = Guid.NewGuid();
            db.PublicationSections.Add(new PublicationSection
            {
                Id = id,
                ProjectId = projectId,
                Title = title,
                Kind = kind,
                SystemRole = role,
                Anchor = PublicationSectionAnchor.Front,
                InclusionMode = PublicationSectionInclusionMode.Automatic,
                StartSide = startSide,
                LocalOrder = order,
                ManuscriptJson = ManuscriptCodec.Serialize(CreateSystemDocument(id, role)),
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        if (!added)
            return;

        await TouchBookAsync(projectId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        historyEvents.PublishReviewStateChanged(projectId);
    }

    public async Task<IReadOnlyList<PublicationSectionView>> ListAsync(
        PublicationSectionTarget target,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(target.ProjectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        await EnsureSystemSectionsAsync(target.ProjectId, cancellationToken);
        var core = await db.PublicationSections.AsNoTracking()
            .Where(item => item.ProjectId == target.ProjectId && item.EditionId == null)
            .ToListAsync(cancellationToken);
        if (target.EditionId is not Guid editionId)
            return await ViewsAsync(target, core, inherited: false, cancellationToken);

        var edition = await db.PublicationEditions.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == editionId
                && item.ProjectId == target.ProjectId
                && item.Status != PublicationEditionStatus.Archived, cancellationToken)
            ?? throw new KeyNotFoundException("Publication release was not found or is archived.");
        var orderOverrides = PublicationSectionOrderCodec.Deserialize(edition.PublicationSectionOrderJson);
        var local = await db.PublicationSections.AsNoTracking()
            .Where(item => item.ProjectId == target.ProjectId && item.EditionId == editionId)
            .ToListAsync(cancellationToken);
        var overlays = local.Where(item => item.CoreSectionId.HasValue)
            .ToDictionary(item => item.CoreSectionId!.Value);
        var effective = new List<(PublicationSection Row, bool Inherited, int EffectiveOrder, bool OrderOverridden)>();
        foreach (var section in core)
        {
            if (overlays.TryGetValue(section.Id, out var overlay))
            {
                if (!overlay.IsExcluded)
                    effective.Add(EffectiveRow(overlay, orderOverrides));
            }
            else
                effective.Add(EffectiveRow(section, orderOverrides, inherited: true));
        }
        effective.AddRange(local.Where(item => item.CoreSectionId == null && !item.IsExcluded)
            .Select(item => EffectiveRow(item, orderOverrides)));
        return await ViewsAsync(target, effective, cancellationToken);
    }

    public async Task<PublicationSectionView> GetAsync(
        PublicationSectionTarget target,
        Guid sectionId,
        CancellationToken cancellationToken = default)
    {
        var section = (await ListAsync(target, cancellationToken)).SingleOrDefault(item => item.Id == sectionId)
            ?? throw new KeyNotFoundException("Publication section was not found for this book target.");
        return section;
    }

    public async Task<PublicationSectionView> UpsertAsync(
        PublicationSectionTarget target,
        PublicationSectionInput input,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(target.ProjectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        await EnsureSystemSectionsAsync(target.ProjectId, cancellationToken);
        ValidateAnchor(input.Anchor, input.TargetKind, input.TargetId);
        if (input.Kind is PublicationSectionKind.TitlePage or PublicationSectionKind.Copyright or PublicationSectionKind.Contents
            && input.Id is null)
            throw new InvalidOperationException("Title, copyright, and contents use the existing generated publication sections.");
        var documentId = input.Id ?? Guid.NewGuid();
        var document = input.Id.HasValue
            ? ManuscriptCodec.Deserialize(input.ManuscriptJson, documentId, input.ExpectedRevision ?? 0)
            : ManuscriptCodec.Deserialize(input.ManuscriptJson) with { ManuscriptId = documentId, Revision = 0 };
        ValidateSectionMode(document);
        await ValidateDocumentAsync(target, document, cancellationToken);

        PublicationSection row;
        string? beforeHistory = null;
        string? beforeSectionState = null;
        if (input.Id is Guid id)
        {
            row = await db.PublicationSections.SingleOrDefaultAsync(item => item.Id == id && item.ProjectId == target.ProjectId, cancellationToken)
                ?? throw new KeyNotFoundException("Publication section was not found.");
            if (row.EditionId != target.EditionId)
            {
                if (target.EditionId is Guid editionId && row.EditionId == null)
                    return await CustomizeAndApplyUnderLeaseAsync(target.ProjectId, editionId, row, input, document, cancellationToken);
                throw new InvalidOperationException("The publication section belongs to a different content target.");
            }
            if (input.ExpectedRevision is long expected && row.Revision != expected)
                throw new DbUpdateConcurrencyException($"Publication section changed (expected revision {expected}, current {row.Revision}).");
            if (row.SystemRole != PublicationSectionSystemRole.None && row.Kind != input.Kind)
                throw new InvalidOperationException("A generated publication section cannot change its system role.");
            if (row.SystemRole == PublicationSectionSystemRole.None
                && input.Kind is PublicationSectionKind.TitlePage or PublicationSectionKind.Copyright or PublicationSectionKind.Contents)
                throw new InvalidOperationException("Title, copyright, and contents use the existing generated publication sections.");
            ValidateSystemDocument(row.SystemRole, input, document);
            if (row.Anchor != input.Anchor || row.TargetKind != input.TargetKind || row.TargetId != input.TargetId)
                row.LocalOrder = await NextOrderAsync(target, input.Anchor, input.TargetId, cancellationToken);
            var previousDocument = ManuscriptCodec.Deserialize(row.ManuscriptJson, row.Id, row.Revision);
            beforeHistory = await AuthoringSnapshotCodec.CaptureManuscriptAsync(
                db, previousDocument, target.ProjectId, null, row.Id, target.EditionId, cancellationToken);
            beforeSectionState = CaptureSectionState(row);
        }
        else
        {
            if (target.EditionId is Guid editionId)
                await RequireEditionAsync(target.ProjectId, editionId, cancellationToken);
            row = new PublicationSection
            {
                Id = documentId,
                ProjectId = target.ProjectId,
                EditionId = target.EditionId,
                LocalOrder = await NextOrderAsync(target, input.Anchor, input.TargetId, cancellationToken),
            };
            db.PublicationSections.Add(row);
        }

        Apply(row, input, document);
        await SyncPlacementReferencesAsync(
            db,
            row,
            ManuscriptCodec.Deserialize(row.ManuscriptJson, row.Id, row.Revision),
            cancellationToken);
        await TouchTargetAsync(target, cancellationToken);
        string? afterHistory = null;
        if (beforeHistory is not null)
        {
            var committedDocument = ManuscriptCodec.Deserialize(row.ManuscriptJson, row.Id, row.Revision);
            afterHistory = await AuthoringSnapshotCodec.CaptureManuscriptAsync(
                db, committedDocument, target.ProjectId, null, row.Id, target.EditionId, cancellationToken);
            if (string.Equals(beforeHistory, afterHistory, StringComparison.Ordinal)
                && string.Equals(beforeSectionState, CaptureSectionState(row), StringComparison.Ordinal))
            {
                db.ChangeTracker.Clear();
                return await GetStoredAsync(target, row.Id, cancellationToken);
            }
        }
        var invalidateTargets = beforeHistory is null || authoringMutationContext.IsHistorySuppressed
            ? []
            : new[]
            {
                target.EditionId.HasValue
                    ? $"release:{target.EditionId.Value:D}:section:{row.Id:D}"
                    : $"publication-section:{row.Id:D}"
            };
        if (invalidateTargets.Length > 0)
        {
            await authoringGenerations.StageInvalidationAsync(
                db,
                target.ProjectId,
                invalidateTargets,
                cancellationToken);
        }
        await db.SaveChangesAsync(cancellationToken);
        authoringGenerations.CompleteInvalidation(invalidateTargets);
        if (!authoringMutationContext.IsHistorySuppressed)
            historyEvents.PublishReviewStateChanged(target.ProjectId);
        return await GetStoredAsync(target, row.Id, cancellationToken);
    }

    public async Task<PublicationSectionView> SetInclusionAsync(
        PublicationSectionTarget target,
        Guid sectionId,
        PublicationSectionInclusionMode inclusionMode,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(inclusionMode))
            throw new ArgumentOutOfRangeException(nameof(inclusionMode));

        await using var databaseOperation = await database.OpenWriteAsync(target.ProjectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var row = await db.PublicationSections.SingleOrDefaultAsync(
            item => item.ProjectId == target.ProjectId && item.Id == sectionId,
            cancellationToken) ?? throw new KeyNotFoundException("Publication section was not found.");

        if (row.EditionId != target.EditionId)
        {
            if (target.EditionId is not Guid editionId || row.EditionId is not null)
                throw new InvalidOperationException("The publication section belongs to a different content target.");

            var overlay = await db.PublicationSections.SingleOrDefaultAsync(
                item => item.ProjectId == target.ProjectId
                    && item.EditionId == editionId
                    && item.CoreSectionId == row.Id,
                cancellationToken);
            if (overlay is null)
            {
                if (row.Revision != expectedRevision)
                    throw new DbUpdateConcurrencyException($"Publication section changed (expected revision {expectedRevision}, current {row.Revision}).");

                overlay = await CloneSectionAsync(row, editionId, cancellationToken);
                overlay.InclusionMode = inclusionMode;
                overlay.IsExcluded = false;
                db.PublicationSections.Add(overlay);
            }
            else
            {
                if (overlay.Revision != expectedRevision)
                    throw new DbUpdateConcurrencyException($"Publication section changed (expected revision {expectedRevision}, current {overlay.Revision}).");

                if (!overlay.IsExcluded && overlay.InclusionMode == inclusionMode)
                    return await GetStoredAsync(target, overlay.Id, cancellationToken);

                overlay.InclusionMode = inclusionMode;
                overlay.IsExcluded = false;
                overlay.UpdatedAt = DateTime.UtcNow;
            }

            await TouchEditionAsync(editionId, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            historyEvents.PublishReviewStateChanged(target.ProjectId);
            return await GetStoredAsync(target, overlay.Id, cancellationToken);
        }

        if (row.Revision != expectedRevision)
            throw new DbUpdateConcurrencyException($"Publication section changed (expected revision {expectedRevision}, current {row.Revision}).");
        if (row.IsExcluded)
            throw new InvalidOperationException("An excluded publication section cannot be included through this operation.");
        if (row.InclusionMode == inclusionMode)
            return await GetStoredAsync(target, row.Id, cancellationToken);

        row.InclusionMode = inclusionMode;
        row.UpdatedAt = DateTime.UtcNow;
        await TouchTargetAsync(target, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        historyEvents.PublishReviewStateChanged(target.ProjectId);
        return await GetStoredAsync(target, row.Id, cancellationToken);
    }

    public async Task<PublicationSectionView> PatchManuscriptAsync(
        PublicationSectionTarget target,
        Guid sectionId,
        long expectedRevision,
        IReadOnlyList<ManuscriptOperationInput> operations,
        CancellationToken cancellationToken = default)
    {
        if (operations.Count is < 1 or > 200)
            throw new InvalidOperationException("Apply between 1 and 200 focused manuscript operations at a time.");
        var current = await GetAsync(target, sectionId, cancellationToken);
        if (current.Revision != expectedRevision)
            throw new DbUpdateConcurrencyException($"Publication section changed (expected revision {expectedRevision}, current {current.Revision}).");
        if (current.SystemRole != PublicationSectionSystemRole.None)
            throw new InvalidOperationException("Generated publication sections use their page-layout controls; their manuscript structure is not directly editable.");
        var changed = ManuscriptOperations.Apply(current.Manuscript, ManuscriptOperationInput.ToOperations(operations)).Document;
        // UpsertAsync owns the persisted revision increment. ManuscriptOperations advances its
        // result for direct persistence callers, so keep this draft tied to the revision that
        // the upsert is revision-checking and let Apply stamp the committed next revision.
        var draft = changed with { Revision = expectedRevision };
        return await UpsertAsync(target, new(
            current.Id, current.Title, current.Kind, current.Anchor, current.TargetKind, current.TargetId,
            current.InclusionMode, current.StartSide, ManuscriptCodec.Serialize(draft), expectedRevision), cancellationToken);
    }

    public async Task<PublicationSectionView> ApplyAuthoringOperationsAsync(
        PublicationSectionTarget target,
        Guid sectionId,
        long expectedRevision,
        IReadOnlyList<ManuscriptOperation> operations,
        CancellationToken cancellationToken = default)
    {
        var current = await GetAsync(target, sectionId, cancellationToken);
        if (current.Revision != expectedRevision)
            throw new DbUpdateConcurrencyException($"Publication section changed (expected revision {expectedRevision}, current {current.Revision}).");
        if (current.SystemRole != PublicationSectionSystemRole.None)
            throw new InvalidOperationException("Generated publication sections use their page-layout controls; their manuscript structure is not directly editable.");
        var changed = ManuscriptOperations.Apply(current.Manuscript, operations).Document with { Revision = expectedRevision };
        return await UpsertAsync(target, new(
            current.Id, current.Title, current.Kind, current.Anchor, current.TargetKind, current.TargetId,
            current.InclusionMode, current.StartSide, ManuscriptCodec.Serialize(changed), expectedRevision), cancellationToken);
    }

    public Task RefreshAuthoringDerivedStateAsync(
        PublicationSectionTarget target,
        Guid sectionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        historyEvents.PublishReviewStateChanged(target.ProjectId);
        return Task.CompletedTask;
    }

    public async Task<PublicationSectionView> CustomizeAsync(
        Guid projectId,
        Guid editionId,
        Guid coreSectionId,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        await EnsureSystemSectionsAsync(projectId, cancellationToken);
        var core = await db.PublicationSections.AsNoTracking()
            .SingleOrDefaultAsync(item => item.ProjectId == projectId && item.EditionId == null && item.Id == coreSectionId, cancellationToken)
            ?? throw new KeyNotFoundException("Core publication section was not found.");
        var existing = await db.PublicationSections.AsNoTracking()
            .SingleOrDefaultAsync(item => item.EditionId == editionId && item.CoreSectionId == coreSectionId, cancellationToken);
        if (existing is not null)
            return await GetStoredAsync(new(projectId, editionId), existing.Id, cancellationToken);
        await RequireEditionAsync(projectId, editionId, cancellationToken);
        var clone = await CloneSectionAsync(core, editionId, cancellationToken);
        db.PublicationSections.Add(clone);
        await TouchEditionAsync(editionId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        historyEvents.PublishReviewStateChanged(projectId);
        return await GetStoredAsync(new(projectId, editionId), clone.Id, cancellationToken);
    }

    public async Task<PublicationSectionView> EnsureSystemDesignedPageAsync(
        PublicationSectionTarget target,
        Guid sectionId,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(target.ProjectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var section = await db.PublicationSections.SingleOrDefaultAsync(item => item.ProjectId == target.ProjectId
            && item.EditionId == target.EditionId && item.Id == sectionId, cancellationToken)
            ?? throw new KeyNotFoundException("Publication section was not found for this book target.");
        if (section.SystemRole is not (PublicationSectionSystemRole.Title or PublicationSectionSystemRole.Copyright))
            return await GetStoredAsync(target, section.Id, cancellationToken);
        var document = ManuscriptCodec.Deserialize(section.ManuscriptJson, section.Id, section.Revision);
        if (document.Content.Count > 0 && document.Content.All(block => block.Type == ManuscriptBlockType.DesignedPage))
        {
            var currentValues = await BindingValuesAsync(target, cancellationToken);
            var pageIds = document.Content.Select(block => block.DesignedPageId!.Value).Distinct().ToList();
            var contents = await db.DesignedPageContents
                .Where(item => item.ProjectId == target.ProjectId
                    && item.EditionId == target.EditionId
                    && pageIds.Contains(item.DesignedPageId))
                .ToListAsync(cancellationToken);
            var changed = ApplyResolvedBindings(contents, currentValues) > 0;
            if (changed)
            {
                await db.SaveChangesAsync(cancellationToken);
                historyEvents.PublishReviewStateChanged(target.ProjectId);
            }
            return await GetStoredAsync(target, section.Id, cancellationToken);
        }

        var values = await BindingValuesAsync(target, cancellationToken);
        var requiredFields = section.SystemRole == PublicationSectionSystemRole.Title
            ? new[] { PublicationBoundField.Title, PublicationBoundField.Subtitle, PublicationBoundField.Author }
            : new[] { PublicationBoundField.Copyright, PublicationBoundField.Publisher, PublicationBoundField.Isbn };
        var sourceBlocks = requiredFields.Select(field =>
            document.Content.FirstOrDefault(block => block.PublicationField == field)
                ?? BoundBlock(field, field == PublicationBoundField.Title
                    ? ManuscriptStyleRoles.ChapterHeading
                    : ManuscriptStyleRoles.Body)).ToList();
        var contentId = Guid.NewGuid();
        var semanticBlocks = sourceBlocks.Select((block, index) => block with
        {
            Id = $"section-text-{Guid.NewGuid():N}",
            Content = [new ManuscriptInline { Text = PublicationTextBindings.NormalizeSemanticText(values.GetValueOrDefault(block.PublicationField!.Value, string.Empty)) }],
            Type = index == 0 && section.SystemRole == PublicationSectionSystemRole.Title
                ? ManuscriptBlockType.Heading
                : ManuscriptBlockType.Paragraph,
            HeadingLevel = index == 0 && section.SystemRole == PublicationSectionSystemRole.Title ? 1 : null,
        }).ToList();
        var semantic = new ManuscriptDocument { ManuscriptId = contentId, Content = semanticBlocks };
        CompositionScene scene;
        if (target.EditionId is Guid editionId)
            scene = DesignedPageService.CreatePageScene(
                (await effectiveConfigurations.ResolveReleaseAsync(target.ProjectId, editionId, cancellationToken)).Edition);
        else
            scene = DesignedPageService.CreatePageScene(await db.ProjectPageSetups.AsNoTracking()
                .SingleAsync(item => item.ProjectId == target.ProjectId, cancellationToken));
        var layerId = scene.Layers[0].Id;
        var objects = semanticBlocks.Select((block, index) => new CompositionObject
        {
            Id = Guid.NewGuid(),
            LayerId = layerId,
            Kind = CompositionObjectKind.Text,
            Name = sourceBlocks[index].PublicationField!.Value.ToString(),
            Bounds = SystemTextBounds(section.SystemRole, index),
            ZIndex = index,
            ContentReferences = [new ManuscriptRangeReference(block.Id)],
            FontFamilyKey = "builtin:lora",
            FontWeight = index == 0 && section.SystemRole == PublicationSectionSystemRole.Title ? 700 : 400,
            FontSizePoints = SystemTextSize(section.SystemRole, index),
            LineHeight = 1.2,
            TextAlignment = section.SystemRole == PublicationSectionSystemRole.Title
                ? CompositionTextAlignment.Center
                : CompositionTextAlignment.Start,
            SemanticRole = block.Type == ManuscriptBlockType.Heading
                ? CompositionSemanticRole.Heading1
                : CompositionSemanticRole.Paragraph,
            ReadingOrder = index + 1,
        }).ToList();
        scene = scene with { Objects = objects };
        DesignedPageService.Validate(scene, semantic);
        var contentTarget = target.EditionId is Guid releaseId
            ? EditorContentTarget.ForEdition(releaseId)
            : EditorContentTarget.Core;
        _ = await designedPages.CreateAndPlaceAsync(
            contentTarget,
            target.ProjectId,
            DesignedPageContainer.PublicationSection(section.Id),
            0,
            section.Title,
            section.Revision,
            semanticBlocks: semantic.Content,
            authoredScene: scene,
            replaceContainerContent: true,
            cancellationToken: cancellationToken);
        await TouchTargetAsync(target, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        historyEvents.PublishReviewStateChanged(target.ProjectId);
        return await GetStoredAsync(target, section.Id, cancellationToken);
    }

    private static CompositionBounds SystemTextBounds(PublicationSectionSystemRole role, int index) => role switch
    {
        PublicationSectionSystemRole.Title when index == 0 => new() { XPercent = 12, YPercent = 28, WidthPercent = 76, HeightPercent = 20 },
        PublicationSectionSystemRole.Title when index == 1 => new() { XPercent = 16, YPercent = 49, WidthPercent = 68, HeightPercent = 10 },
        PublicationSectionSystemRole.Title => new() { XPercent = 20, YPercent = 68, WidthPercent = 60, HeightPercent = 8 },
        _ => new() { XPercent = 12, YPercent = 58 + index * 8, WidthPercent = 76, HeightPercent = 7 },
    };

    private static double SystemTextSize(PublicationSectionSystemRole role, int index) =>
        role == PublicationSectionSystemRole.Title ? index == 0 ? 32 : index == 1 ? 18 : 14 : 10;

    public async Task ResetAsync(Guid projectId, Guid editionId, Guid sectionId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var row = await db.PublicationSections
            .SingleOrDefaultAsync(item => item.ProjectId == projectId && item.EditionId == editionId && item.Id == sectionId, cancellationToken)
            ?? throw new KeyNotFoundException("Release publication section was not found.");
        if (row.CoreSectionId is null)
            throw new InvalidOperationException("A release-only section cannot be reset to Core Book.");
        var historyTarget = $"release:{editionId:D}:section:{row.Id:D}";
        var placementReferences = await db.DesignedPagePlacementReferences
            .Where(item => item.ProjectId == projectId
                && item.ContainerKind == DesignedPageContainerKind.PublicationSection
                && item.ContainerId == row.Id)
            .ToListAsync(cancellationToken);
        db.DesignedPagePlacementReferences.RemoveRange(placementReferences);
        db.PublicationSections.Remove(row);
        await TouchEditionAsync(editionId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        historyEvents.PublishReviewStateChanged(projectId);
        deltaHistory.Clear(historyTarget);
    }

    public async Task DeleteAsync(PublicationSectionTarget target, Guid sectionId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(target.ProjectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var row = await db.PublicationSections.SingleOrDefaultAsync(item => item.ProjectId == target.ProjectId && item.Id == sectionId, cancellationToken);
        if (row is null)
            return;
        if (target.EditionId is Guid editionId && row.EditionId == null)
        {
            var overlay = await db.PublicationSections.SingleOrDefaultAsync(item => item.EditionId == editionId && item.CoreSectionId == row.Id, cancellationToken);
            if (overlay is null)
            {
                overlay = new PublicationSection
                {
                    ProjectId = target.ProjectId,
                    EditionId = editionId,
                    CoreSectionId = row.Id,
                    IsExcluded = true,
                    Title = row.Title,
                    Kind = row.Kind,
                    SystemRole = row.SystemRole,
                    Anchor = row.Anchor,
                    TargetKind = row.TargetKind,
                    TargetId = row.TargetId,
                    ActId = row.ActId,
                    ChapterId = row.ChapterId,
                    InclusionMode = PublicationSectionInclusionMode.Omitted,
                    StartSide = row.StartSide,
                    LocalOrder = row.LocalOrder,
                    ManuscriptJson = row.ManuscriptJson,
                    Revision = row.Revision,
                };
                db.PublicationSections.Add(overlay);
            }
            else if (overlay.IsExcluded)
                return;
            else
                overlay.IsExcluded = true;
            await TouchEditionAsync(editionId, cancellationToken);
        }
        else
        {
            if (row.EditionId != target.EditionId)
                throw new InvalidOperationException("The publication section belongs to another target.");
            if (row.SystemRole != PublicationSectionSystemRole.None)
                throw new InvalidOperationException("Generated publication sections can be omitted but not deleted.");
            var historyTarget = row.EditionId.HasValue
                ? $"release:{row.EditionId.Value:D}:section:{row.Id:D}"
                : $"publication-section:{row.Id:D}";
            var placementReferences = await db.DesignedPagePlacementReferences
                .Where(item => item.ProjectId == target.ProjectId
                    && item.ContainerKind == DesignedPageContainerKind.PublicationSection
                    && item.ContainerId == row.Id)
                .ToListAsync(cancellationToken);
            db.DesignedPagePlacementReferences.RemoveRange(placementReferences);
            db.PublicationSections.Remove(row);
            await TouchTargetAsync(target, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            historyEvents.PublishReviewStateChanged(target.ProjectId);
            deltaHistory.Clear(historyTarget);
            return;
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        historyEvents.PublishReviewStateChanged(target.ProjectId);
    }

    public async Task ReorderWithinAnchorAsync(
        PublicationSectionTarget target,
        IReadOnlyList<Guid> orderedSectionIds,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(target.ProjectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        if (orderedSectionIds.Count == 0 || orderedSectionIds.Count != orderedSectionIds.Distinct().Count())
            throw new InvalidOperationException("Section order must contain unique section IDs.");
        var effective = await ListAsync(target, cancellationToken);
        var selected = effective.Where(item => orderedSectionIds.Contains(item.Id)).ToList();
        if (selected.Count != orderedSectionIds.Count)
            throw new InvalidOperationException("One or more publication sections were not found.");
        var first = selected[0];
        if (selected.Any(item => item.Anchor != first.Anchor || item.TargetId != first.TargetId)
            || effective.Count(item => item.Anchor == first.Anchor && item.TargetId == first.TargetId) != selected.Count)
            throw new InvalidOperationException("Reorder every section at one anchor together.");
        if (target.EditionId is Guid editionId)
        {
            var edition = await db.PublicationEditions.AsNoTracking().SingleOrDefaultAsync(item => item.Id == editionId
                && item.ProjectId == target.ProjectId
                && item.Status != PublicationEditionStatus.Archived, cancellationToken)
                ?? throw new KeyNotFoundException("Publication release was not found or is archived.");
            var orderOverrides = PublicationSectionOrderCodec.Deserialize(edition.PublicationSectionOrderJson);
            foreach (var (id, index) in orderedSectionIds.Select((id, index) => (id, index)))
            {
                var view = selected.Single(item => item.Id == id);
                orderOverrides[view.CoreSectionId ?? view.Id] = index;
            }
            var serializedOrder = PublicationSectionOrderCodec.Serialize(orderOverrides);
            if (string.Equals(edition.PublicationSectionOrderJson, serializedOrder, StringComparison.Ordinal))
                return;

            var updated = await db.PublicationEditions
                .Where(item => item.Id == edition.Id && item.Revision == edition.Revision)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.PublicationSectionOrderJson, serializedOrder)
                    .SetProperty(item => item.Revision, item => item.Revision + 1)
                    .SetProperty(item => item.UpdatedAt, DateTime.UtcNow), cancellationToken);
            if (updated != 1)
                throw new DbUpdateConcurrencyException("The publication release changed while its sections were being reordered. Refresh and try again.");
        }
        else
        {
            var rows = await db.PublicationSections
                .Where(item => item.ProjectId == target.ProjectId
                    && item.EditionId == null
                    && orderedSectionIds.Contains(item.Id))
                .ToListAsync(cancellationToken);
            var requested = orderedSectionIds.Select((id, index) => (id, index))
                .ToDictionary(item => item.id, item => item.index);
            if (rows.All(row => row.LocalOrder == requested[row.Id]))
                return;

            foreach (var row in rows)
            {
                row.LocalOrder = requested[row.Id];
                row.UpdatedAt = DateTime.UtcNow;
            }
            await TouchBookAsync(target.ProjectId, cancellationToken);
        }
        await db.SaveChangesAsync(cancellationToken);
        historyEvents.PublishReviewStateChanged(target.ProjectId);
    }

    public async Task<string> ResolveBoundFieldAsync(
        PublicationSectionTarget target,
        PublicationBoundField field,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        if (target.EditionId is null)
        {
            var book = await db.PublicationBooks.AsNoTracking()
                .SingleAsync(item => item.ProjectId == target.ProjectId, cancellationToken);
            return field switch
            {
                PublicationBoundField.Title => book.Title,
                PublicationBoundField.Subtitle => book.Subtitle,
                PublicationBoundField.Author => book.Author,
                PublicationBoundField.Publisher => book.Publisher,
                PublicationBoundField.Copyright => book.Copyright,
                PublicationBoundField.Description => book.Description,
                PublicationBoundField.Isbn => string.Empty,
                _ => string.Empty,
            };
        }
        var effective = await effectiveConfigurations.ResolveReleaseAsync(target.ProjectId, target.EditionId.Value, cancellationToken);
        return field switch
        {
            PublicationBoundField.Title => effective.Edition.TitleOverride,
            PublicationBoundField.Subtitle => effective.Edition.Subtitle,
            PublicationBoundField.Author => effective.Edition.Author,
            PublicationBoundField.Publisher => effective.Edition.Publisher,
            PublicationBoundField.Copyright => effective.Edition.Copyright,
            PublicationBoundField.Description => effective.Edition.Description,
            PublicationBoundField.Isbn => effective.Edition.Isbn,
            _ => string.Empty,
        };
    }

    public async Task<PublicationSectionDesignedPageResult> CreateDesignedPageAsync(
        PublicationSectionTarget target,
        Guid sectionId,
        int blockIndex,
        string name,
        DesignedPageLayoutMode layoutMode,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(target.ProjectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var section = await db.PublicationSections.SingleOrDefaultAsync(item => item.ProjectId == target.ProjectId && item.Id == sectionId, cancellationToken)
            ?? throw new KeyNotFoundException("Publication section was not found.");
        if (section.EditionId != target.EditionId)
            throw new InvalidOperationException("Customize the inherited section before adding a release-specific Designed Page.");
        if (section.Revision != expectedRevision)
            throw new DbUpdateConcurrencyException($"Publication section changed (expected revision {expectedRevision}, current {section.Revision}).");
        var document = ManuscriptCodec.Deserialize(section.ManuscriptJson, section.Id, section.Revision);
        if (document.Content.Any(block => block.Type != ManuscriptBlockType.DesignedPage))
            throw new InvalidOperationException("A publication section is either a prose section or a designed-page section. Add a separate designed-page section instead of mixing page canvases into prose.");
        if (blockIndex < 0 || blockIndex > document.Content.Count)
            throw new InvalidOperationException("The Designed Page insertion point is outside the section.");

        CompositionScene scene;
        if (target.EditionId is Guid editionId)
        {
            var effective = await effectiveConfigurations.ResolveReleaseAsync(target.ProjectId, editionId, cancellationToken);
            scene = DesignedPageService.CreatePageScene(effective.Edition, layoutMode);
        }
        else
        {
            var setup = await db.ProjectPageSetups.AsNoTracking().SingleAsync(item => item.ProjectId == target.ProjectId, cancellationToken);
            scene = DesignedPageService.CreatePageScene(setup, layoutMode);
        }
        var contentTarget = target.EditionId is Guid releaseId
            ? EditorContentTarget.ForEdition(releaseId)
            : EditorContentTarget.Core;
        var placement = await designedPages.CreateAndPlaceAsync(
            contentTarget,
            target.ProjectId,
            DesignedPageContainer.PublicationSection(section.Id),
            blockIndex,
            name,
            expectedRevision,
            new DesignedPageInitialContent { LayoutMode = layoutMode },
            authoredScene: scene,
            cancellationToken: cancellationToken);
        await TouchTargetAsync(target, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        historyEvents.PublishReviewStateChanged(target.ProjectId);
        return new(await GetStoredAsync(target, section.Id, cancellationToken), placement.Page, placement.PlacementId);
    }

    internal static ManuscriptDocument ResolveBindings(ManuscriptDocument document, IReadOnlyDictionary<PublicationBoundField, string> values) =>
        document with
        {
            Content = document.Content.Select(block => block.PublicationField is { } field
                ? block with
                {
                    Content = [new ManuscriptInline { Text = PublicationTextBindings.NormalizeSemanticText(values.GetValueOrDefault(field, string.Empty)) }],
                }
                : block).ToList(),
        };

    internal static async Task<int> RefreshBindingsAsync(
        AppDbContext db,
        PublicationSectionTarget target,
        IReadOnlyDictionary<PublicationBoundField, string> values,
        CancellationToken cancellationToken = default)
    {
        var contents = await db.DesignedPageContents
            .Where(item => item.ProjectId == target.ProjectId && item.EditionId == target.EditionId)
            .ToListAsync(cancellationToken);
        return ApplyResolvedBindings(contents, values);
    }

    private static int ApplyResolvedBindings(
        IReadOnlyList<DesignedPageContent> contents,
        IReadOnlyDictionary<PublicationBoundField, string> values)
    {
        var changed = 0;
        foreach (var content in contents)
        {
            // Binding refreshes update semantic content and its owning revision
            // together. Accept and repair the one legacy revision drift.
            var current = ManuscriptCodec.Deserialize(content.SemanticManuscriptJson);
            ManuscriptCodec.Validate(current, content.Id, current.Revision);
            if (current.Revision > content.Revision)
                throw new InvalidDataException("A publication page manuscript is newer than its owning content revision.");
            var resolved = ResolveBindings(current, values);
            var contentChanged = !ManuscriptCodec.ContentEquals(current, resolved);
            var revisionDrifted = current.Revision != content.Revision;
            if (!contentChanged && !revisionDrifted)
                continue;

            if (contentChanged)
                content.Revision = checked(content.Revision + 1);
            content.SemanticManuscriptJson = ManuscriptCodec.Serialize(
                resolved with { Revision = content.Revision });
            changed++;
        }
        return changed;
    }

    private async Task<IReadOnlyList<PublicationSectionView>> ViewsAsync(
        PublicationSectionTarget target,
        IReadOnlyList<PublicationSection> rows,
        bool inherited,
        CancellationToken cancellationToken) =>
        await ViewsAsync(target, rows.Select(item => (item, inherited, item.LocalOrder, false)).ToList(), cancellationToken);

    private async Task<IReadOnlyList<PublicationSectionView>> ViewsAsync(
        PublicationSectionTarget target,
        IReadOnlyList<(PublicationSection Row, bool Inherited, int EffectiveOrder, bool OrderOverridden)> rows,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var actTitles = await db.Acts.AsNoTracking().Where(item => item.ProjectId == target.ProjectId)
            .OrderBy(item => item.Order)
            .ToDictionaryAsync(item => item.Id, item => item.Title, cancellationToken);
        var chapterTitles = await db.Chapters.AsNoTracking().Where(item => item.ProjectId == target.ProjectId)
            .OrderBy(item => item.Order)
            .ToDictionaryAsync(item => item.Id, item => item.Title, cancellationToken);
        var bindingValues = await BindingValuesAsync(target, cancellationToken);
        var views = rows.OrderBy(item => AnchorOrder(item.Row.Anchor))
            .ThenBy(item => item.Row.TargetId is Guid targetId
                ? item.Row.TargetKind == PublishOutlineTargetKind.Act
                    ? actTitles.Keys.ToList().IndexOf(targetId)
                    : chapterTitles.Keys.ToList().IndexOf(targetId)
                : -1)
            .ThenBy(item => item.EffectiveOrder)
            .ThenBy(item => item.Row.Id)
            .Select(item => View(item.Row, item.Inherited, item.EffectiveOrder, item.OrderOverridden, actTitles, chapterTitles, bindingValues))
            .ToList();
        return views;
    }

    private async Task<IReadOnlyDictionary<PublicationBoundField, string>> BindingValuesAsync(
        PublicationSectionTarget target,
        CancellationToken cancellationToken)
    {
        if (target.EditionId is null)
        {
            var book = await books.GetOrCreateAsync(target.ProjectId, cancellationToken);
            return new Dictionary<PublicationBoundField, string>
            {
                [PublicationBoundField.Title] = book.Title,
                [PublicationBoundField.Subtitle] = book.Subtitle,
                [PublicationBoundField.Author] = book.Author,
                [PublicationBoundField.Publisher] = book.Publisher,
                [PublicationBoundField.Copyright] = book.Copyright,
                [PublicationBoundField.Description] = book.Description,
                [PublicationBoundField.Isbn] = string.Empty,
            };
        }
        var effective = await effectiveConfigurations.ResolveReleaseAsync(
            target.ProjectId, target.EditionId.Value, cancellationToken);
        return new Dictionary<PublicationBoundField, string>
        {
            [PublicationBoundField.Title] = effective.Edition.TitleOverride,
            [PublicationBoundField.Subtitle] = effective.Edition.Subtitle,
            [PublicationBoundField.Author] = effective.Edition.Author,
            [PublicationBoundField.Publisher] = effective.Edition.Publisher,
            [PublicationBoundField.Copyright] = effective.Edition.Copyright,
            [PublicationBoundField.Description] = effective.Edition.Description,
            [PublicationBoundField.Isbn] = effective.Edition.Isbn,
        };
    }

    private static PublicationSectionView View(
        PublicationSection row,
        bool inherited,
        int effectiveOrder,
        bool orderOverridden,
        IReadOnlyDictionary<Guid, string> acts,
        IReadOnlyDictionary<Guid, string> chapters,
        IReadOnlyDictionary<PublicationBoundField, string> bindingValues)
    {
        var document = ResolveBindings(
            ManuscriptCodec.Deserialize(row.ManuscriptJson, row.Id, row.Revision), bindingValues);
        var targetTitle = row.TargetId is not Guid targetId
            ? string.Empty
            : row.TargetKind == PublishOutlineTargetKind.Act
                ? acts.GetValueOrDefault(targetId, "Missing act")
                : chapters.GetValueOrDefault(targetId, "Missing chapter");
        return new(
            row.Id, row.CoreSectionId, row.EditionId, row.Title, row.Kind, row.SystemRole, row.Anchor,
            row.TargetKind, row.TargetId, targetTitle, row.InclusionMode, row.StartSide,
            row.InclusionMode != PublicationSectionInclusionMode.Omitted && !row.IsExcluded,
            inherited, orderOverridden, effectiveOrder, document, row.Revision,
            document.Content.Count(block => block.Type == ManuscriptBlockType.DesignedPage),
            document.Content.Count(block => block.Type == ManuscriptBlockType.Figure));
    }

    private static (PublicationSection Row, bool Inherited, int EffectiveOrder, bool OrderOverridden) EffectiveRow(
        PublicationSection row,
        IReadOnlyDictionary<Guid, int> orderOverrides,
        bool inherited = false)
    {
        var identity = row.CoreSectionId ?? row.Id;
        return orderOverrides.TryGetValue(identity, out var order)
            ? (row, inherited, order, true)
            : (row, inherited, row.LocalOrder, false);
    }

    private async Task<PublicationSectionView> CustomizeAndApplyUnderLeaseAsync(
        Guid projectId,
        Guid editionId,
        PublicationSection core,
        PublicationSectionInput input,
        ManuscriptDocument document,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var clone = await CloneSectionAsync(core, editionId, cancellationToken);
        if (clone.Anchor != input.Anchor || clone.TargetKind != input.TargetKind || clone.TargetId != input.TargetId)
            clone.LocalOrder = await NextOrderAsync(new(projectId, editionId), input.Anchor, input.TargetId, cancellationToken);
        Apply(clone, input with { Id = clone.Id, ExpectedRevision = clone.Revision }, document with { ManuscriptId = clone.Id });
        await SyncPlacementReferencesAsync(
            db,
            clone,
            ManuscriptCodec.Deserialize(clone.ManuscriptJson, clone.Id, clone.Revision),
            cancellationToken);
        db.PublicationSections.Add(clone);
        await TouchEditionAsync(editionId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        historyEvents.PublishReviewStateChanged(projectId);
        return await GetStoredAsync(new(projectId, editionId), clone.Id, cancellationToken);
    }

    private async Task<PublicationSectionView> GetStoredAsync(
        PublicationSectionTarget target,
        Guid sectionId,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var row = await db.PublicationSections.AsNoTracking()
            .SingleOrDefaultAsync(item => item.ProjectId == target.ProjectId
                && item.EditionId == target.EditionId
                && item.Id == sectionId, cancellationToken)
            ?? throw new KeyNotFoundException("Publication section was not found for this book target.");
        return (await ViewsAsync(target, [row], inherited: false, cancellationToken)).Single();
    }

    private async Task<PublicationSection> CloneSectionAsync(PublicationSection core, Guid editionId, CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var clone = new PublicationSection
        {
            Id = Guid.NewGuid(),
            ProjectId = core.ProjectId,
            EditionId = editionId,
            CoreSectionId = core.Id,
            Title = core.Title,
            Kind = core.Kind,
            SystemRole = core.SystemRole,
            Anchor = core.Anchor,
            TargetKind = core.TargetKind,
            TargetId = core.TargetId,
            ActId = core.ActId,
            ChapterId = core.ChapterId,
            InclusionMode = core.InclusionMode,
            StartSide = core.StartSide,
            LocalOrder = core.LocalOrder,
            Revision = core.Revision,
        };
        var document = ManuscriptCodec.Deserialize(core.ManuscriptJson, core.Id, core.Revision);
        clone.ManuscriptJson = ManuscriptCodec.Serialize(document with
        {
            ManuscriptId = clone.Id,
            Content = document.Content.Select(block => block.Type == ManuscriptBlockType.DesignedPage
                ? block with { Id = $"designed-page-{Guid.NewGuid():N}" }
                : block).ToList(),
        });
        await SyncPlacementReferencesAsync(
            db,
            clone,
            ManuscriptCodec.Deserialize(clone.ManuscriptJson, clone.Id, clone.Revision),
            cancellationToken);
        return clone;
    }

    private async Task ValidateDocumentAsync(PublicationSectionTarget target, ManuscriptDocument document, CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        ManuscriptStyleService.ValidateDocumentReferences(
            document, await manuscriptStyles.ListAsync(target.ProjectId, cancellationToken));
        var imageIds = document.Content
            .Where(item => item.Type == ManuscriptBlockType.Figure && item.ImageId.HasValue)
            .Select(item => item.ImageId!.Value)
            .Distinct()
            .ToList();
        if (imageIds.Count > 0 && await db.PublishAssets.AsNoTracking().CountAsync(
            item => item.ProjectId == target.ProjectId && imageIds.Contains(item.Id), cancellationToken) != imageIds.Count)
            throw new InvalidDataException("The publication section references an image outside this project.");
        var pageIds = document.Content.Where(item => item.DesignedPageId.HasValue)
            .Select(item => item.DesignedPageId!.Value).Distinct().ToList();
        if (pageIds.Count == 0)
            return;
        var pages = await db.DesignedPages.AsNoTracking()
            .Include(item => item.Contents)
            .Where(item => pageIds.Contains(item.Id) && item.ProjectId == target.ProjectId)
            .ToListAsync(cancellationToken);
        if (pages.Count != pageIds.Count
            || pages.Any(page => page.ScopeEditionId is Guid scopeId && scopeId != target.EditionId)
            || pages.Any(page => !page.Contents.Any(content => content.EditionId == target.EditionId)
                && !page.Contents.Any(content => content.EditionId == null)))
            throw new InvalidDataException("The publication section references a Designed Page outside its current book target.");
    }

    private static async Task SyncPlacementReferencesAsync(
        AppDbContext db,
        PublicationSection section,
        ManuscriptDocument document,
        CancellationToken cancellationToken)
    {
        var desired = document.Content.Where(block => block.Type == ManuscriptBlockType.DesignedPage).ToList();
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
                && item.EditionId == section.EditionId))
            .Distinct()
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

    private static void ValidateSectionMode(ManuscriptDocument document)
    {
        var hasDesignedPages = document.Content.Any(block => block.Type == ManuscriptBlockType.DesignedPage);
        if (hasDesignedPages && document.Content.Any(block => block.Type != ManuscriptBlockType.DesignedPage))
            throw new InvalidOperationException("A publication section is either a prose section or a designed-page section. Put prose and page canvases in separate publication sections.");
        if (hasDesignedPages && document.Content.Any(block => !block.DesignedPageId.HasValue))
            throw new InvalidOperationException("Every block in a designed-page publication section must reference a page canvas.");
    }

    private static void Apply(PublicationSection row, PublicationSectionInput input, ManuscriptDocument document)
    {
        row.Title = string.IsNullOrWhiteSpace(input.Title) ? "Section" : input.Title.Trim();
        row.Kind = input.Kind;
        row.Anchor = input.Anchor;
        row.TargetKind = input.TargetKind;
        row.TargetId = input.TargetId;
        row.ActId = input.TargetKind == PublishOutlineTargetKind.Act ? input.TargetId : null;
        row.ChapterId = input.TargetKind == PublishOutlineTargetKind.Chapter ? input.TargetId : null;
        row.InclusionMode = input.InclusionMode;
        row.StartSide = input.StartSide;
        row.Revision = checked(row.Revision + 1);
        row.ManuscriptJson = ManuscriptCodec.Serialize(document with
        {
            ManuscriptId = row.Id,
            Revision = row.Revision,
            Content = document.Content.Select(block => block.PublicationField is null
                ? block
                : block with { Content = [new ManuscriptInline { Text = string.Empty }] }).ToList(),
        });
        row.UpdatedAt = DateTime.UtcNow;
    }

    private static string CaptureSectionState(PublicationSection row) =>
        AuthoringSnapshotCodec.Serialize(new
        {
            row.Title,
            row.Kind,
            row.Anchor,
            row.TargetKind,
            row.TargetId,
            row.ActId,
            row.ChapterId,
            row.InclusionMode,
            row.StartSide,
            row.IsExcluded,
            row.LocalOrder,
        });

    private static void ValidateAnchor(PublicationSectionAnchor anchor, PublishOutlineTargetKind? kind, Guid? targetId)
    {
        var requiresTarget = anchor is not (PublicationSectionAnchor.Front or PublicationSectionAnchor.Back);
        if (requiresTarget != targetId.HasValue || requiresTarget != kind.HasValue)
            throw new InvalidOperationException(requiresTarget
                ? "This section position requires an act or chapter target."
                : "Front and back sections cannot have an outline target.");
        if (anchor is PublicationSectionAnchor.BeforeAct or PublicationSectionAnchor.AfterAct && kind != PublishOutlineTargetKind.Act)
            throw new InvalidOperationException("The selected section position requires an act target.");
        if (anchor is PublicationSectionAnchor.BeforeChapter or PublicationSectionAnchor.AfterChapter && kind != PublishOutlineTargetKind.Chapter)
            throw new InvalidOperationException("The selected section position requires a chapter target.");
    }

    private static void ValidateSystemDocument(
        PublicationSectionSystemRole role,
        PublicationSectionInput input,
        ManuscriptDocument document)
    {
        if (role == PublicationSectionSystemRole.None)
            return;
        if (input.Anchor != PublicationSectionAnchor.Front || input.TargetId is not null || input.TargetKind is not null)
            throw new InvalidOperationException("Generated title, copyright, and contents sections remain in the front of the book.");
        if (role is PublicationSectionSystemRole.Title or PublicationSectionSystemRole.Copyright
            && document.Content.Count > 0
            && document.Content.All(block => block.Type == ManuscriptBlockType.DesignedPage
                && block.DesignedPageId.HasValue))
            return;
        var expected = role switch
        {
            PublicationSectionSystemRole.Title => new[] { PublicationBoundField.Title, PublicationBoundField.Subtitle, PublicationBoundField.Author },
            PublicationSectionSystemRole.Copyright =>
                [PublicationBoundField.Copyright, PublicationBoundField.Publisher, PublicationBoundField.Isbn],
            PublicationSectionSystemRole.Contents => [],
            _ => [],
        };
        var actual = document.Content.Where(block => block.PublicationField.HasValue)
            .Select(block => block.PublicationField!.Value).ToArray();
        if (!actual.SequenceEqual(expected))
            throw new InvalidOperationException("Linked Core Book fields in a generated publication section cannot be removed or rebound.");
    }

    private async Task<int> NextOrderAsync(
        PublicationSectionTarget target,
        PublicationSectionAnchor anchor,
        Guid? targetId,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var localMaximum = await db.PublicationSections
            .Where(item => item.ProjectId == target.ProjectId && item.EditionId == target.EditionId
                && item.Anchor == anchor && item.TargetId == targetId)
            .Select(item => (int?)item.LocalOrder)
            .MaxAsync(cancellationToken) ?? -1;
        if (target.EditionId is null)
            return localMaximum + 1;
        var inheritedMaximum = await db.PublicationSections
            .Where(item => item.ProjectId == target.ProjectId && item.EditionId == null
                && item.Anchor == anchor && item.TargetId == targetId)
            .Select(item => (int?)item.LocalOrder)
            .MaxAsync(cancellationToken) ?? -1;
        return Math.Max(localMaximum, inheritedMaximum) + 1;
    }

    private async Task RequireEditionAsync(Guid projectId, Guid editionId, CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        if (!await db.PublicationEditions.AsNoTracking().AnyAsync(item => item.Id == editionId && item.ProjectId == projectId && item.Status != PublicationEditionStatus.Archived, cancellationToken))
            throw new KeyNotFoundException("Publication release was not found or is archived.");
    }

    private async Task TouchTargetAsync(PublicationSectionTarget target, CancellationToken cancellationToken)
    {
        if (target.EditionId is Guid editionId)
            await TouchEditionAsync(editionId, cancellationToken);
        else
            await TouchBookAsync(target.ProjectId, cancellationToken);
    }

    private async Task TouchBookAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        await db.PublicationBooks.Where(item => item.ProjectId == projectId).ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.Revision, item => item.Revision + 1)
                    .SetProperty(item => item.UpdatedAt, DateTime.UtcNow), cancellationToken);
    }
    private async Task TouchEditionAsync(Guid editionId, CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        await db.PublicationEditions.Where(item => item.Id == editionId).ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.Revision, item => item.Revision + 1)
                    .SetProperty(item => item.UpdatedAt, DateTime.UtcNow), cancellationToken);
    }
    private static string RemapDocumentId(string json, Guid id)
    {
        var document = ManuscriptCodec.Deserialize(json);
        return ManuscriptCodec.Serialize(document with { ManuscriptId = id });
    }

    private static ManuscriptDocument CreateSystemDocument(Guid id, PublicationSectionSystemRole role)
    {
        List<ManuscriptBlock> blocks = role switch
        {
            PublicationSectionSystemRole.Title =>
            [
                BoundBlock(PublicationBoundField.Title, ManuscriptStyleRoles.ChapterHeading),
                BoundBlock(PublicationBoundField.Subtitle, ManuscriptStyleRoles.Subheading),
                BoundBlock(PublicationBoundField.Author, ManuscriptStyleRoles.Body),
            ],
            PublicationSectionSystemRole.Copyright =>
            [
                BoundBlock(PublicationBoundField.Copyright, ManuscriptStyleRoles.Body),
                BoundBlock(PublicationBoundField.Publisher, ManuscriptStyleRoles.Body),
                BoundBlock(PublicationBoundField.Isbn, ManuscriptStyleRoles.Body),
            ],
            PublicationSectionSystemRole.Contents => [],
            _ => [],
        };
        return new ManuscriptDocument { ManuscriptId = id, Content = blocks };
    }

    private static ManuscriptBlock BoundBlock(PublicationBoundField field, string styleRole) => new()
    {
        Id = $"field-{field.ToString().ToLowerInvariant()}-{Guid.NewGuid():N}",
        Type = ManuscriptBlockType.Paragraph,
        StyleRole = styleRole,
        PublicationField = field,
        Content = [new ManuscriptInline { Text = string.Empty }],
    };

    private static int AnchorOrder(PublicationSectionAnchor anchor) => anchor switch
    {
        PublicationSectionAnchor.Front => 0,
        PublicationSectionAnchor.BeforeAct => 1,
        PublicationSectionAnchor.BeforeChapter => 2,
        PublicationSectionAnchor.AfterChapter => 3,
        PublicationSectionAnchor.AfterAct => 4,
        PublicationSectionAnchor.Back => 5,
        _ => 6,
    };

    private static readonly (PublicationSectionSystemRole Role, PublicationSectionKind Kind, string Title, int Order, PublicationSectionStartSide StartSide)[] SystemSectionDefinitions =
    [
        (PublicationSectionSystemRole.Title, PublicationSectionKind.TitlePage, "Title page", 0, PublicationSectionStartSide.Recto),
        (PublicationSectionSystemRole.Copyright, PublicationSectionKind.Copyright, "Copyright", 1, PublicationSectionStartSide.Verso),
        (PublicationSectionSystemRole.Contents, PublicationSectionKind.Contents, "Contents", 2, PublicationSectionStartSide.Recto),
    ];
}
