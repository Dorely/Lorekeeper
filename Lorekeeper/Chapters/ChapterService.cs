using Lorekeeper.Authoring;
using Lorekeeper.Context;
using Lorekeeper.EditorChat;
using Lorekeeper.Graph;
using Lorekeeper.Knowledge;
using Lorekeeper.Llm;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Outline;
using Lorekeeper.Persistence;
using Lorekeeper.Persistence.Repositories;
using Lorekeeper.Search;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Chapters;

public class ChapterService(
    IVectorStore vectors, IEmbeddingService embeddings, ITextChunker chunker, IProjectSearchIndex projectSearch, IGraphAutoLinkService autoLinks, IOutlineGraphSync outlineGraphSync, IContextIndexingService contextIndexing, IVectorIndexWorkCoordinator indexWork, IAppDatabaseOperationFactory database, IManuscriptStyleService manuscriptStyles, IChapterSemanticProjectionService semanticProjection, IProjectMutationCoordinator projectMutations, IAuthoringHistoryRuntime authoringHistory, IAuthoringMutationContextAccessor authoringMutationContext, IManuscriptAnnotationService annotations, IEditorContestMutationGuard contestGuard, IEditorContestMutationContext contestMutationContext, ILogger<ChapterService> logger) : IChapterService, IManuscriptService
{
    public async Task<IReadOnlyList<Chapter>> ListAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var repo = databaseOperation.Repositories.Chapters;
        return await repo.ListByProjectAsync(projectId, cancellationToken);
    }
    public async Task<Chapter?> GetAsync(Guid chapterId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var repo = databaseOperation.Repositories.Chapters;
        return await repo.GetByIdAsync(chapterId, cancellationToken);
    }
    public async Task<Chapter?> ReloadFromStoreAsync(Guid chapterId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var repo = databaseOperation.Repositories.Chapters;
        return await repo.ReloadFromStoreAsync(chapterId, cancellationToken);
    }
    public async Task<Chapter> CreateAsync(Guid projectId, Guid? actId = null, string? title = null, string? synopsis = null, Guid? id = null, CancellationToken cancellationToken = default)
    {
        await contestGuard.EnsureMutationAllowedAsync(projectId, cancellationToken);
        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var repo = databaseOperation.Repositories.Chapters;
        var projects = databaseOperation.Repositories.Projects;
        var project = await projects.GetByIdAsync(projectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project {projectId} not found.");

        var nextOrder = await repo.GetMaxOrderAsync(projectId, actId, cancellationToken) + 1;
        var resolvedTitle = string.IsNullOrWhiteSpace(title)
            ? $"Chapter {nextOrder + 1}"
            : title.Trim();

        var chapter = new Chapter
        {
            Id = id ?? Guid.NewGuid(),
            ProjectId = projectId,
            ActId = actId,
            Title = resolvedTitle,
            ManuscriptRevision = 0,
            Synopsis = synopsis ?? string.Empty,
            Order = nextOrder,
            VectorIndexState = VectorIndexState.UpToDate, // empty body == nothing to index
        };
        chapter.ManuscriptJson = ManuscriptCodec.Serialize(
            ManuscriptCodec.CreateEmpty(chapter.Id, chapter.ManuscriptRevision));

        await repo.AddAsync(chapter, cancellationToken);
        project.UpdatedAt = DateTime.UtcNow;
        projects.Update(project);
        await databaseOperation.SaveChangesAsync(cancellationToken);
        await databaseOperation.DisposeAsync();
        await outlineGraphSync.EnsureChapterAsync(chapter, cancellationToken);
        await contextIndexing.ReindexChapterAsync(chapter.Id, cancellationToken);
        if (chapter.ActId is Guid createdActId)
            await contextIndexing.ReindexActAsync(createdActId, cancellationToken);
        return chapter;
    }

    public async Task<Chapter> UpdateAsync(
        Guid chapterId,
        string? title = null,
        string? synopsis = null,
        ChapterActAssignment? actId = null,
        CancellationToken cancellationToken = default)
    {
        var projectId = await GetChapterProjectIdAsync(chapterId, cancellationToken);
        await contestGuard.EnsureMutationAllowedAsync(projectId, cancellationToken);
        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var repo = databaseOperation.Repositories.Chapters;
        var projects = databaseOperation.Repositories.Projects;
        var chapter = await repo.GetByIdAsync(chapterId, cancellationToken)
            ?? throw new InvalidOperationException($"Chapter {chapterId} not found.");
        var previousActId = chapter.ActId;

        var titleOrSynopsisChanged = false;
        var actChanged = false;
        if (title is not null && title != chapter.Title)
        {
            chapter.Title = title;
            titleOrSynopsisChanged = true;
        }
        if (synopsis is not null && synopsis != chapter.Synopsis)
        {
            chapter.Synopsis = synopsis;
            titleOrSynopsisChanged = true;
        }
        if (actId is { } assignment && assignment.Value != chapter.ActId)
        {
            chapter.ActId = assignment.Value;
            // Append to the end of the destination bucket so we don't collide with existing orders.
            chapter.Order = await repo.GetMaxOrderAsync(chapter.ProjectId, assignment.Value, cancellationToken) + 1;
            actChanged = true;
        }

        if (!titleOrSynopsisChanged && !actChanged)
            return chapter;

        chapter.UpdatedAt = DateTime.UtcNow;
        repo.Update(chapter);

        var project = await projects.GetByIdAsync(chapter.ProjectId, cancellationToken);
        if (project is not null)
        {
            project.UpdatedAt = DateTime.UtcNow;
            projects.Update(project);
        }

        await databaseOperation.SaveChangesAsync(cancellationToken);
        await databaseOperation.DisposeAsync();

        await outlineGraphSync.EnsureChapterAsync(chapter, cancellationToken);

        await contextIndexing.ReindexChapterAsync(chapter.Id, cancellationToken);

        var actIdsToReindex = new HashSet<Guid>();
        if (titleOrSynopsisChanged && chapter.ActId is Guid currentActId)
            actIdsToReindex.Add(currentActId);
        if (actChanged && previousActId is Guid oldActId)
            actIdsToReindex.Add(oldActId);
        if (actChanged && chapter.ActId is Guid newActId)
            actIdsToReindex.Add(newActId);

        foreach (var actToReindex in actIdsToReindex)
            await contextIndexing.ReindexActAsync(actToReindex, cancellationToken);

        return chapter;
    }

    public async Task<ManuscriptSnapshot?> GetManuscriptAsync(
        EditorContentTarget target,
        Guid chapterId,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var repo = databaseOperation.Repositories.Chapters;
        var chapter = await repo.GetByIdAsync(chapterId, cancellationToken);
        if (chapter is null)
            return null;
        return target.IsCore
            ? await SnapshotAsync(chapter, cancellationToken)
            : await EditionSnapshotAsync(target, chapter, cancellationToken);
    }

    public Task<ManuscriptHistoryMutationResult> UndoAsync(
        EditorContentTarget target,
        Guid chapterId,
        CancellationToken cancellationToken = default) =>
        MoveHistoryAsync(target, chapterId, redo: false, cancellationToken);

    public Task<ManuscriptHistoryMutationResult> RedoAsync(
        EditorContentTarget target,
        Guid chapterId,
        CancellationToken cancellationToken = default) =>
        MoveHistoryAsync(target, chapterId, redo: true, cancellationToken);

    private async Task<ManuscriptHistoryMutationResult> MoveHistoryAsync(
        EditorContentTarget target,
        Guid chapterId,
        bool redo,
        CancellationToken cancellationToken)
    {
        var projectId = await GetChapterProjectIdAsync(chapterId, cancellationToken);
        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        await contestGuard.EnsureMutationAllowedAsync(projectId, cancellationToken);
        var db = databaseOperation.Db;
        var repo = databaseOperation.Repositories.Chapters;
        var chapter = await repo.GetByIdAsync(chapterId, cancellationToken)
            ?? throw new KeyNotFoundException("The chapter was not found.");
        EditionManuscriptState? editionState = null;
        var current = target.IsCore
            ? ManuscriptCodec.Deserialize(chapter.ManuscriptJson, chapter.Id, chapter.ManuscriptRevision)
            : (editionState = await GetRequiredEditionStateWithoutRevisionAsync(target, chapter, cancellationToken)).Document;
        var currentPayload = await AuthoringSnapshotCodec.CaptureManuscriptAsync(
            db, current, chapter.ProjectId, chapter.Id, null, target.EditionId, cancellationToken,
            inherited: editionState?.Override is null && !target.IsCore);
        var historyTarget = HistoryTarget(target, chapter);
        var result = redo
            ? await authoringHistory.RedoAsync(historyTarget, currentPayload,
                (payload, ct) => RestoreHistorySnapshotAsync(target, chapter, payload, ct), cancellationToken)
            : await authoringHistory.UndoAsync(historyTarget, currentPayload,
                (payload, ct) => RestoreHistorySnapshotAsync(target, chapter, payload, ct), cancellationToken);
        await RefreshDerivedStateAsync(target, chapterId, cancellationToken);
        var snapshot = await GetManuscriptAsync(target, chapterId, cancellationToken)
            ?? throw new KeyNotFoundException("The restored chapter was not found.");
        return new ManuscriptHistoryMutationResult(snapshot, result.State, result.ActionLabel, result.SelectionJson);
    }

    private async Task RestoreHistorySnapshotAsync(
        EditorContentTarget target,
        Chapter chapter,
        string payload,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var repo = databaseOperation.Repositories.Chapters;
        var snapshot = AuthoringSnapshotCodec.ReadManuscript(payload);
        var source = ManuscriptCodec.Deserialize(snapshot.ManuscriptJson);
        long nextRevision;
        PublicationEditionChapterOverride? editionOverride = null;
        PublicationEdition? edition = null;
        if (target.IsCore)
        {
            nextRevision = checked(chapter.ManuscriptRevision + 1);
        }
        else
        {
            edition = await RequireEditableEditionAsync(target, chapter.ProjectId, cancellationToken);
            editionOverride = await db.PublicationEditionChapterOverrides.SingleOrDefaultAsync(
                item => item.EditionId == edition.Id && item.ChapterId == chapter.Id,
                cancellationToken);
            if (snapshot.Inherited)
            {
                var editionCompositions = await db.PageCompositions
                    .Include(item => item.Variants)
                    .Where(item => item.ProjectId == chapter.ProjectId
                        && item.ChapterId == chapter.Id
                        && item.EditionId == edition.Id
                        && item.DetachedAt == null)
                    .ToListAsync(cancellationToken);
                var detachedAt = DateTime.UtcNow;
                foreach (var composition in editionCompositions)
                {
                    composition.DetachedAt = detachedAt;
                    foreach (var variant in composition.Variants)
                        variant.DetachedAt = detachedAt;
                }
                if (editionOverride is not null)
                    db.PublicationEditionChapterOverrides.Remove(editionOverride);
                edition.Revision = checked(edition.Revision + 1);
                edition.UpdatedAt = DateTime.UtcNow;
                var inherited = ManuscriptCodec.Deserialize(chapter.ManuscriptJson, chapter.Id, chapter.ManuscriptRevision);
                await annotations.RebaseForManuscriptMutationAsync(
                    chapter.ProjectId, target, chapter.Id, inherited, chapter.ManuscriptRevision, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
                return;
            }
            nextRevision = checked((editionOverride?.Revision ?? chapter.ManuscriptRevision) + 1);
        }
        var document = source with { ManuscriptId = chapter.Id, Revision = nextRevision };
        ManuscriptCodec.Validate(document, chapter.Id, nextRevision);
        await ValidateFigureAssetsAsync(chapter.ProjectId, document, cancellationToken);
        await ValidateStyleReferencesAsync(chapter.ProjectId, document, null, cancellationToken);
        await RestoreCompositionsAsync(chapter.ProjectId, chapter.Id, null, target.EditionId, snapshot.Compositions, cancellationToken);

        if (target.IsCore)
        {
            chapter.ManuscriptJson = ManuscriptCodec.Serialize(document);
            chapter.ManuscriptRevision = nextRevision;
            chapter.UpdatedAt = DateTime.UtcNow;
            chapter.VectorIndexState = VectorIndexState.Stale;
            repo.Update(chapter);
        }
        else
        {
            editionOverride ??= new PublicationEditionChapterOverride
            {
                EditionId = edition!.Id,
                ChapterId = chapter.Id,
                BaseCoreRevision = chapter.ManuscriptRevision,
                BaseCoreHash = ManuscriptCodec.HashPlainText(ManuscriptCodec.ProjectPlainText(chapter.Manuscript))
            };
            if (editionOverride.Id == Guid.Empty)
                editionOverride.Id = Guid.NewGuid();
            if (db.Entry(editionOverride).State == EntityState.Detached)
                db.PublicationEditionChapterOverrides.Add(editionOverride);
            editionOverride.ManuscriptJson = ManuscriptCodec.Serialize(document);
            editionOverride.Revision = nextRevision;
            editionOverride.UpdatedAt = DateTime.UtcNow;
            edition!.Revision = checked(edition.Revision + 1);
            edition.UpdatedAt = DateTime.UtcNow;
        }
        await annotations.RebaseForManuscriptMutationAsync(
            chapter.ProjectId, target, chapter.Id, document, nextRevision, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task RestoreCompositionsAsync(
        Guid projectId,
        Guid? chapterId,
        Guid? publicationSectionId,
        Guid? editionId,
        IReadOnlyList<AuthoringCompositionSnapshot> desired,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var current = await db.PageCompositions
            .IgnoreQueryFilters()
            .Include(item => item.Variants)
            .Where(item => item.ProjectId == projectId
                && item.ChapterId == chapterId
                && item.PublicationSectionId == publicationSectionId
                && item.EditionId == editionId)
            .ToListAsync(cancellationToken);
        var desiredIds = desired.Select(item => item.Id).ToHashSet();
        var now = DateTime.UtcNow;
        foreach (var composition in current.Where(item => !desiredIds.Contains(item.Id)))
        {
            composition.DetachedAt = now;
            foreach (var variant in composition.Variants)
                variant.DetachedAt = now;
        }
        foreach (var item in desired)
        {
            var composition = current.SingleOrDefault(value => value.Id == item.Id);
            if (composition is null)
            {
                composition = new PageComposition { Id = item.Id, ProjectId = projectId };
                db.PageCompositions.Add(composition);
                current.Add(composition);
            }
            composition.ChapterId = chapterId;
            composition.PublicationSectionId = publicationSectionId;
            composition.EditionId = editionId;
            composition.SourceCompositionId = item.SourceCompositionId;
            composition.Name = item.Name;
            composition.DetachedAt = null;
            composition.UpdatedAt = now;
            composition.Revision = checked(composition.Revision + 1);
            var semantic = ManuscriptCodec.Deserialize(item.SemanticManuscriptJson) with
            {
                ManuscriptId = item.Id,
                Revision = composition.Revision
            };
            composition.SemanticManuscriptJson = ManuscriptCodec.Serialize(semantic);
            composition.ActiveAuthoringVariantId = item.ActiveAuthoringVariantId;
            var variantIds = item.Variants.Select(value => value.Id).ToHashSet();
            foreach (var variant in composition.Variants.Where(value => !variantIds.Contains(value.Id)))
                variant.DetachedAt = now;
            foreach (var desiredVariant in item.Variants)
            {
                var variant = composition.Variants.SingleOrDefault(value => value.Id == desiredVariant.Id);
                if (variant is null)
                {
                    variant = new PageCompositionVariant { Id = desiredVariant.Id, Composition = composition, CompositionId = composition.Id };
                    composition.Variants.Add(variant);
                }
                variant.GeometryKey = desiredVariant.GeometryKey;
                variant.SceneJson = desiredVariant.SceneJson;
                variant.DetachedAt = null;
                variant.UpdatedAt = now;
                variant.Revision = checked(variant.Revision + 1);
            }
        }
    }

    private static AuthoringHistoryTarget HistoryTarget(EditorContentTarget target, Chapter chapter) =>
        target.IsCore
            ? new AuthoringHistoryTarget(chapter.ProjectId, AuthoringHistoryDocumentKind.CoreChapter, chapter.Id)
            : new AuthoringHistoryTarget(chapter.ProjectId, AuthoringHistoryDocumentKind.EditionChapter, chapter.Id, target.EditionId);

    public async Task<ManuscriptMutationResult> ReplaceDocumentAsync(
        EditorContentTarget target,
        Guid chapterId,
        long expectedRevision,
        ManuscriptDocument document,
        CancellationToken cancellationToken = default)
    {
        var projectId = await GetChapterProjectIdAsync(chapterId, cancellationToken);
        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        await contestGuard.EnsureMutationAllowedAsync(projectId, cancellationToken);
        if (!target.IsCore)
            return await ReplaceEditionDocumentAsync(target, chapterId, expectedRevision, document, cancellationToken);
        var chapter = await RequireRevisionAsync(chapterId, expectedRevision, cancellationToken);
        var replacement = document with
        {
            ManuscriptId = chapter.Id,
            Revision = checked(chapter.ManuscriptRevision + 1),
        };
        ManuscriptCodec.Validate(replacement, chapter.Id, replacement.Revision);
        var current = ManuscriptCodec.Deserialize(
            chapter.ManuscriptJson,
            chapter.Id,
            chapter.ManuscriptRevision);
        if (EquivalentContent(current, replacement))
            return new ManuscriptMutationResult(await SnapshotAsync(chapter, cancellationToken), []);
        return await SaveManuscriptAsync(
            chapter,
            replacement,
            replacement.Content.Select(block => block.Id).ToList(),
            cancellationToken);
    }

    public async Task<ManuscriptMutationResult> ApplyAsync(
        EditorContentTarget target,
        Guid chapterId,
        long expectedRevision,
        IReadOnlyList<ManuscriptOperation> operations,
        CancellationToken cancellationToken = default)
    {
        var projectId = await GetChapterProjectIdAsync(chapterId, cancellationToken);
        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        await contestGuard.EnsureMutationAllowedAsync(projectId, cancellationToken);
        if (!target.IsCore)
            return await ApplyEditionAsync(target, chapterId, expectedRevision, operations, acquireLease: true, cancellationToken);
        var chapter = await RequireRevisionAsync(chapterId, expectedRevision, cancellationToken);
        var source = ManuscriptCodec.Deserialize(
            chapter.ManuscriptJson,
            chapter.Id,
            chapter.ManuscriptRevision);
        var (document, changed) = ManuscriptOperations.Apply(source, operations);
        if (changed.Count == 0 || EquivalentContent(source, document))
            return new ManuscriptMutationResult(await SnapshotAsync(chapter, cancellationToken), []);
        return await SaveManuscriptAsync(chapter, document, changed, cancellationToken);
    }

    public async Task<ManuscriptMutationResult> ApplyUnderProjectMutationLeaseAsync(
        EditorContentTarget target,
        Guid chapterId,
        long expectedRevision,
        IReadOnlyList<ManuscriptOperation> operations,
        CancellationToken cancellationToken = default)
    {
        var projectId = await GetChapterProjectIdAsync(chapterId, cancellationToken);
        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        await contestGuard.EnsureMutationAllowedAsync(projectId, cancellationToken);
        if (!target.IsCore)
            return await ApplyEditionAsync(target, chapterId, expectedRevision, operations, acquireLease: false, cancellationToken);
        var chapter = await RequireRevisionAsync(chapterId, expectedRevision, cancellationToken);
        var source = ManuscriptCodec.Deserialize(
            chapter.ManuscriptJson,
            chapter.Id,
            chapter.ManuscriptRevision);
        var (document, changed) = ManuscriptOperations.Apply(source, operations);
        if (changed.Count == 0 || EquivalentContent(source, document))
            return new ManuscriptMutationResult(await SnapshotAsync(chapter, cancellationToken), []);
        var persisted = await PersistManuscriptUnderLeaseAsync(chapter, document, changed, cancellationToken);
        return persisted.Result;
    }

    public async Task<ManuscriptMutationResult> ApplyPersistedUnderProjectMutationLeaseAsync(
        EditorContentTarget target,
        Guid chapterId,
        long expectedRevision,
        IReadOnlyList<ManuscriptOperation> operations,
        CancellationToken cancellationToken = default)
    {
        var projectId = await GetChapterProjectIdAsync(chapterId, cancellationToken);
        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        await contestGuard.EnsureMutationAllowedAsync(projectId, cancellationToken);
        if (!target.IsCore)
            return await ApplyEditionAsync(target, chapterId, expectedRevision, operations, acquireLease: false, cancellationToken);
        var chapter = await RequireRevisionAsync(chapterId, expectedRevision, cancellationToken);
        var source = ManuscriptCodec.Deserialize(chapter.ManuscriptJson, chapter.Id, chapter.ManuscriptRevision);
        var (document, changed) = ManuscriptOperations.Apply(source, operations);
        if (changed.Count == 0 || EquivalentContent(source, document))
            return new ManuscriptMutationResult(await SnapshotAsync(chapter, cancellationToken), []);
        var persisted = await PersistManuscriptUnderLeaseAsync(chapter, document, changed, cancellationToken);
        return persisted.Result;
    }

    public async Task RefreshDerivedStateAsync(
        EditorContentTarget target,
        Guid chapterId,
        CancellationToken cancellationToken = default)
    {
        var projectId = await GetChapterProjectIdAsync(chapterId, cancellationToken);
        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var repo = databaseOperation.Repositories.Chapters;
        if (!target.IsCore)
        {
            await databaseOperation.DisposeAsync();
            await TryReindexEditionBodyAsync(target, chapterId, cancellationToken);
            return;
        }
        var chapter = await repo.ReloadFromStoreAsync(chapterId, cancellationToken)
            ?? throw new InvalidOperationException($"Chapter {chapterId} not found.");
        await databaseOperation.DisposeAsync();
        await outlineGraphSync.EnsureChapterAsync(chapter, cancellationToken);
        await TryReindexBodyAsync(chapter.Id, cancellationToken);
    }

    public async Task ValidateDocumentReferencesAsync(
        EditorContentTarget target,
        Guid chapterId,
        ManuscriptDocument document,
        IReadOnlyList<ManuscriptStyleView>? styleCatalog = null,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var repo = databaseOperation.Repositories.Chapters;
        var chapter = await repo.GetByIdAsync(chapterId, cancellationToken)
            ?? throw new InvalidOperationException($"Chapter {chapterId} not found.");
        await ValidateFigureAssetsAsync(chapter.ProjectId, document, cancellationToken);
        await ValidateStyleReferencesAsync(
            chapter.ProjectId,
            document,
            styleCatalog,
            cancellationToken);
        await ValidateDesignedPageReferencesAsync(chapter, document, target, cancellationToken);
    }

    private async Task<Chapter> RequireRevisionAsync(
        Guid chapterId,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var repo = databaseOperation.Repositories.Chapters;
        var chapter = await repo.GetByIdAsync(chapterId, cancellationToken)
            ?? throw new InvalidOperationException($"Chapter {chapterId} not found.");
        if (chapter.ManuscriptRevision != expectedRevision)
            throw new ManuscriptRevisionConflictException(expectedRevision, chapter.ManuscriptRevision);
        return chapter;
    }

    private async Task<ManuscriptMutationResult> SaveManuscriptAsync(
        Chapter chapter,
        ManuscriptDocument document,
        IReadOnlyList<string> changedBlockIds,
        CancellationToken cancellationToken)
    {
        // ReplaceDocumentAsync and ApplyAsync already hold the project-scoped
        // database operation. Re-acquiring the same project semaphore here would
        // deadlock contest resolution, which intentionally invokes replacement
        // through that shared operation.
        var persisted = await PersistManuscriptUnderLeaseAsync(
            chapter,
            document,
            changedBlockIds,
            cancellationToken);

        if (!contestMutationContext.IsAuthorized(persisted.Chapter.ProjectId))
            await RefreshPersistedManuscriptAsync(persisted.Chapter, cancellationToken);
        return persisted.Result;
    }

    private async Task RefreshPersistedManuscriptAsync(
        Chapter savedChapter,
        CancellationToken cancellationToken)
    {
        try
        {
            await outlineGraphSync.EnsureChapterAsync(savedChapter, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Chapter {ChapterId} manuscript committed at revision {Revision}, but graph synchronization failed.",
                savedChapter.Id,
                savedChapter.ManuscriptRevision);
        }
        try
        {
            await TryReindexBodyAsync(savedChapter.Id, cancellationToken);
        }
        catch (OperationCanceledException exception)
        {
            logger.LogWarning(
                exception,
                "Chapter {ChapterId} manuscript committed at revision {Revision}, but post-commit indexing was cancelled.",
                savedChapter.Id,
                savedChapter.ManuscriptRevision);
        }
    }

    private async Task<(ManuscriptMutationResult Result, Chapter Chapter)> PersistManuscriptUnderLeaseAsync(
        Chapter chapter,
        ManuscriptDocument document,
        IReadOnlyList<string> changedBlockIds,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var projects = databaseOperation.Repositories.Projects;
        var repo = databaseOperation.Repositories.Chapters;
        chapter = await repo.GetByIdAsync(chapter.Id, cancellationToken)
            ?? throw new InvalidOperationException($"Chapter {chapter.Id} not found.");
        var expectedRevision = checked(document.Revision - 1);
        if (chapter.ManuscriptRevision != expectedRevision)
            throw new ManuscriptRevisionConflictException(expectedRevision, chapter.ManuscriptRevision);
        await ValidateFigureAssetsAsync(chapter.ProjectId, document, cancellationToken);
        await ValidateStyleReferencesAsync(chapter.ProjectId, document, null, cancellationToken);
        await ValidateDesignedPageReferencesAsync(chapter, document, EditorContentTarget.Core, cancellationToken);
        var previous = ManuscriptCodec.Deserialize(chapter.ManuscriptJson, chapter.Id, chapter.ManuscriptRevision);
        var beforeHistory = await AuthoringSnapshotCodec.CaptureManuscriptAsync(
            db, previous, chapter.ProjectId, chapter.Id, null, null, cancellationToken);
        var removedCompositionIds = DesignedPageIds(previous).Except(DesignedPageIds(document)).ToList();
        chapter.ManuscriptJson = ManuscriptCodec.Serialize(document);
        chapter.ManuscriptRevision = document.Revision;
        chapter.UpdatedAt = DateTime.UtcNow;
        chapter.VectorIndexState = VectorIndexState.Stale;

        await annotations.RebaseForManuscriptMutationAsync(
            chapter.ProjectId, EditorContentTarget.Core, chapter.Id, document, document.Revision, cancellationToken);

        var project = await projects.GetByIdAsync(chapter.ProjectId, cancellationToken);
        if (project is not null)
        {
            project.UpdatedAt = DateTime.UtcNow;
            projects.Update(project);
        }

        if (removedCompositionIds.Count > 0)
        {
            var removed = await db.PageCompositions
                .Where(composition => composition.ProjectId == chapter.ProjectId
                    && composition.ChapterId == chapter.Id
                    && composition.DetachedAt == null
                    && removedCompositionIds.Contains(composition.Id))
                .ToListAsync(cancellationToken);
            var detachedAt = DateTime.UtcNow;
            foreach (var composition in removed)
            {
                composition.DetachedAt = detachedAt;
                foreach (var variant in await db.PageCompositionVariants
                    .Where(item => item.CompositionId == composition.Id && item.DetachedAt == null)
                    .ToListAsync(cancellationToken))
                    variant.DetachedAt = detachedAt;
            }
        }

        var afterHistory = await AuthoringSnapshotCodec.CaptureManuscriptAsync(
            db, document, chapter.ProjectId, chapter.Id, null, null, cancellationToken);
        var target = new AuthoringHistoryTarget(chapter.ProjectId, AuthoringHistoryDocumentKind.CoreChapter, chapter.Id);
        var context = authoringMutationContext.Current;

        try
        {
            await databaseOperation.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            var current = await repo.ReloadFromStoreAsync(chapter.Id, cancellationToken);
            throw new ManuscriptRevisionConflictException(
                checked(document.Revision - 1),
                current?.ManuscriptRevision ?? document.Revision);
        }

        if (!contestMutationContext.IsAuthorized(chapter.ProjectId))
        {
            if (context?.IsAssistant == true)
            {
                await authoringHistory.ResetToCurrentAsync(target, afterHistory, CancellationToken.None);
            }
            else
            {
                await authoringHistory.RecordManualActionAsync(
                    target,
                    beforeHistory,
                    afterHistory,
                    AuthoringSnapshotCodec.DescribeManuscriptAction(beforeHistory, afterHistory, "chapter"),
                    cancellationToken: CancellationToken.None);
            }
        }
        return (new ManuscriptMutationResult(await SnapshotAsync(chapter, cancellationToken), changedBlockIds), chapter);
    }

    private async Task ValidateFigureAssetsAsync(
        Guid projectId,
        ManuscriptDocument document,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var imageIds = document.Content
            .Where(block => block.Type == ManuscriptBlockType.Figure)
            .Select(block => block.ImageId!.Value)
            .Distinct()
            .ToList();
        if (imageIds.Count > 0)
        {
            var foundCount = await db.PublishAssets
                .AsNoTracking()
                .CountAsync(image => image.ProjectId == projectId
                    && imageIds.Contains(image.Id)
                    && (image.ContentType == "image/png" || image.ContentType == "image/jpeg"), cancellationToken);
            if (foundCount != imageIds.Count)
            {
                throw new InvalidOperationException(
                    "One or more figure images were not found in this project or are not publication-compatible PNG/JPEG assets.");
            }
        }
    }

    private async Task ValidateDesignedPageReferencesAsync(
        Chapter chapter,
        ManuscriptDocument document,
        EditorContentTarget target,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var ids = DesignedPageIds(document);
        if (ids.Count != document.Content.Count(block => block.Type == ManuscriptBlockType.DesignedPage))
            throw new InvalidDataException("Each Designed Page composition may be referenced exactly once in its chapter.");
        if (ids.Count == 0)
            return;
        var found = await db.PageCompositions.AsNoTracking()
            .CountAsync(composition => composition.ProjectId == chapter.ProjectId
                && composition.ChapterId == chapter.Id
                && composition.EditionId == target.EditionId
                && composition.DetachedAt == null
                && ids.Contains(composition.Id), cancellationToken);
        if (found != ids.Count)
            throw new InvalidDataException("Every Designed Page must reference a composition owned by this chapter and project.");
    }

    private static HashSet<Guid> DesignedPageIds(ManuscriptDocument document) =>
        document.Content
            .Where(block => block.Type == ManuscriptBlockType.DesignedPage)
            .Select(block => block.PageCompositionId!.Value)
            .ToHashSet();

    private async Task ValidateStyleReferencesAsync(
        Guid projectId,
        ManuscriptDocument document,
        IReadOnlyList<ManuscriptStyleView>? styleCatalog,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var styles = styleCatalog
            ?? await manuscriptStyles.ListAsync(projectId, cancellationToken);
        ManuscriptStyleService.ValidateDocumentReferences(document, styles);
        var directFontKeys = document.Content
            .Select(block => block.ParagraphPresentation?.FontFamilyKey)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var projectFontIds = directFontKeys
            .Where(key => key.StartsWith("project:", StringComparison.OrdinalIgnoreCase))
            .Select(key => Guid.TryParse(key["project:".Length..], out var id) ? id : Guid.Empty)
            .ToHashSet();
        if (projectFontIds.Contains(Guid.Empty))
            throw new InvalidDataException("A paragraph references an invalid project font family.");
        if (projectFontIds.Count > 0)
        {
            var found = await db.ProjectFontFamilies.AsNoTracking()
                .CountAsync(family => family.ProjectId == projectId && projectFontIds.Contains(family.Id), cancellationToken);
            if (found != projectFontIds.Count)
                throw new InvalidDataException("A paragraph references a project font family that is not available in this project.");
        }
    }

    private async Task<ManuscriptSnapshot> SnapshotAsync(Chapter chapter, CancellationToken cancellationToken)
    {
        var document = ManuscriptCodec.Deserialize(
            chapter.ManuscriptJson,
            chapter.Id,
            chapter.ManuscriptRevision);
        var plainText = await semanticProjection.ExpandPlainTextAsync(chapter, cancellationToken);
        return new ManuscriptSnapshot(
            chapter.Id,
            chapter.ManuscriptRevision,
            ManuscriptCodec.HashPlainText(plainText),
            plainText,
            document);
    }

    private async Task<ManuscriptSnapshot> EditionSnapshotAsync(
        EditorContentTarget target,
        Chapter chapter,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var edition = await RequireEditableEditionAsync(target, chapter.ProjectId, cancellationToken);
        var chapterOverride = await db.PublicationEditionChapterOverrides.AsNoTracking()
            .SingleOrDefaultAsync(item => item.EditionId == edition.Id && item.ChapterId == chapter.Id, cancellationToken);
        if (chapterOverride is null)
            return await SnapshotAsync(chapter, cancellationToken);
        var document = ManuscriptCodec.Deserialize(chapterOverride.ManuscriptJson, chapter.Id, chapterOverride.Revision);
        var projectedChapter = new Chapter
        {
            Id = chapter.Id,
            ProjectId = chapter.ProjectId,
            Title = chapter.Title,
            Synopsis = chapter.Synopsis,
            Order = chapter.Order,
            ManuscriptJson = chapterOverride.ManuscriptJson,
            ManuscriptRevision = chapterOverride.Revision,
        };
        var plainText = await semanticProjection.ExpandPlainTextAsync(projectedChapter, cancellationToken);
        return new ManuscriptSnapshot(
            chapter.Id,
            chapterOverride.Revision,
            ManuscriptCodec.HashPlainText(plainText),
            plainText,
            document);
    }

    private async Task<ManuscriptMutationResult> ReplaceEditionDocumentAsync(
        EditorContentTarget target,
        Guid chapterId,
        long expectedRevision,
        ManuscriptDocument requested,
        CancellationToken cancellationToken)
    {
        var projectId = await GetChapterProjectIdAsync(chapterId, cancellationToken);
        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var repo = databaseOperation.Repositories.Chapters;
        var chapter = await repo.GetByIdAsync(chapterId, cancellationToken)
            ?? throw new InvalidOperationException($"Chapter {chapterId} not found.");
        var current = await GetRequiredEditionStateAsync(target, chapter, expectedRevision, cancellationToken);
        var normalizedRequested = requested with { ManuscriptId = chapter.Id, Revision = current.Document.Revision };
        ManuscriptCodec.Validate(normalizedRequested, chapter.Id, normalizedRequested.Revision);
        if (EquivalentContent(current.Document, normalizedRequested))
            return new ManuscriptMutationResult(current.Snapshot, []);
        var replacement = normalizedRequested with { Revision = checked(current.Document.Revision + 1) };
        return await PersistEditionDocumentAsync(target, chapter, current.Override, current.Document, replacement,
            replacement.Content.Select(block => block.Id).ToList(), cancellationToken);
    }

    private async Task<ManuscriptMutationResult> ApplyEditionAsync(
        EditorContentTarget target,
        Guid chapterId,
        long expectedRevision,
        IReadOnlyList<ManuscriptOperation> operations,
        bool acquireLease,
        CancellationToken cancellationToken)
    {
        var projectId = await GetChapterProjectIdAsync(chapterId, cancellationToken);
        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var repo = databaseOperation.Repositories.Chapters;
        var chapter = await repo.GetByIdAsync(chapterId, cancellationToken)
            ?? throw new InvalidOperationException($"Chapter {chapterId} not found.");
        if (acquireLease)
        {
            return await ApplyEditionUnderLeaseAsync(target, chapter, expectedRevision, operations, cancellationToken);
        }
        return await ApplyEditionUnderLeaseAsync(target, chapter, expectedRevision, operations, cancellationToken);
    }

    private async Task<ManuscriptMutationResult> ApplyEditionUnderLeaseAsync(
        EditorContentTarget target,
        Chapter chapter,
        long expectedRevision,
        IReadOnlyList<ManuscriptOperation> operations,
        CancellationToken cancellationToken)
    {
        var current = await GetRequiredEditionStateAsync(target, chapter, expectedRevision, cancellationToken);
        var (document, changed) = ManuscriptOperations.Apply(current.Document, operations);
        if (changed.Count == 0 || EquivalentContent(current.Document, document))
            return new ManuscriptMutationResult(current.Snapshot, []);
        return await PersistEditionDocumentAsync(target, chapter, current.Override, current.Document, document, changed, cancellationToken);
    }

    private async Task<ManuscriptMutationResult> PersistEditionDocumentAsync(
        EditorContentTarget target,
        Chapter chapter,
        PublicationEditionChapterOverride? chapterOverride,
        ManuscriptDocument previous,
        ManuscriptDocument requested,
        IReadOnlyList<string> changedBlockIds,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var edition = await RequireEditableEditionAsync(target, chapter.ProjectId, cancellationToken);
        var document = requested with { ManuscriptId = chapter.Id };
        var wasInherited = chapterOverride is null;
        var beforeHistory = await AuthoringSnapshotCodec.CaptureManuscriptAsync(
            db, previous, chapter.ProjectId, chapter.Id, null, edition.Id, cancellationToken,
            inherited: wasInherited);
        if (chapterOverride is null)
        {
            var remap = await CloneReferencedCompositionsAsync(chapter, edition, document, cancellationToken);
            document = RemapCompositions(document, remap);
            previous = RemapCompositions(previous, remap);
            chapterOverride = new PublicationEditionChapterOverride
            {
                EditionId = edition.Id,
                ChapterId = chapter.Id,
                BaseCoreRevision = chapter.ManuscriptRevision,
                BaseCoreHash = ManuscriptCodec.HashPlainText(ManuscriptCodec.ProjectPlainText(chapter.Manuscript)),
                Revision = document.Revision,
            };
            db.PublicationEditionChapterOverrides.Add(chapterOverride);
        }
        ManuscriptCodec.Validate(document, chapter.Id, document.Revision);
        await ValidateFigureAssetsAsync(chapter.ProjectId, document, cancellationToken);
        await ValidateStyleReferencesAsync(chapter.ProjectId, document, null, cancellationToken);
        await ValidateDesignedPageReferencesAsync(chapter, document, target, cancellationToken);
        var removedCompositionIds = DesignedPageIds(previous).Except(DesignedPageIds(document)).ToList();
        if (removedCompositionIds.Count > 0)
        {
            var removed = await db.PageCompositions
                .Where(item => item.EditionId == edition.Id && item.DetachedAt == null
                    && removedCompositionIds.Contains(item.Id))
                .ToListAsync(cancellationToken);
            var detachedAt = DateTime.UtcNow;
            foreach (var composition in removed)
            {
                composition.DetachedAt = detachedAt;
                foreach (var variant in await db.PageCompositionVariants
                    .Where(item => item.CompositionId == composition.Id && item.DetachedAt == null)
                    .ToListAsync(cancellationToken))
                    variant.DetachedAt = detachedAt;
            }
        }
        chapterOverride.ManuscriptJson = ManuscriptCodec.Serialize(document);
        chapterOverride.Revision = document.Revision;
        chapterOverride.UpdatedAt = DateTime.UtcNow;
        edition.UpdatedAt = DateTime.UtcNow;
        edition.Revision = checked(edition.Revision + 1);
        await annotations.RebaseForManuscriptMutationAsync(
            chapter.ProjectId, target, chapter.Id, document, document.Revision, cancellationToken);
        var afterHistory = await AuthoringSnapshotCodec.CaptureManuscriptAsync(
            db, document, chapter.ProjectId, chapter.Id, null, edition.Id, cancellationToken);
        var historyTarget = new AuthoringHistoryTarget(
            chapter.ProjectId,
            AuthoringHistoryDocumentKind.EditionChapter,
            chapter.Id,
            edition.Id);
        var context = authoringMutationContext.Current;

        try
        {
            await databaseOperation.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            var currentRevision = await db.PublicationEditionChapterOverrides.AsNoTracking()
                .Where(item => item.EditionId == edition.Id && item.ChapterId == chapter.Id)
                .Select(item => (long?)item.Revision)
                .SingleOrDefaultAsync(cancellationToken) ?? chapter.ManuscriptRevision;
            throw new ManuscriptRevisionConflictException(checked(document.Revision - 1), currentRevision);
        }

        if (!contestMutationContext.IsAuthorized(chapter.ProjectId))
        {
            if (context?.IsAssistant == true)
            {
                await authoringHistory.ResetToCurrentAsync(historyTarget, afterHistory, CancellationToken.None);
            }
            else
            {
                await authoringHistory.RecordManualActionAsync(
                    historyTarget,
                    beforeHistory,
                    afterHistory,
                    AuthoringSnapshotCodec.DescribeManuscriptAction(beforeHistory, afterHistory, "chapter"),
                    cancellationToken: CancellationToken.None);
            }
        }
        await databaseOperation.DisposeAsync();
        if (!contestMutationContext.IsAuthorized(chapter.ProjectId))
            await TryReindexEditionBodyAsync(target, chapter.Id, cancellationToken);
        return new ManuscriptMutationResult(
            (await EditionSnapshotAsync(target, chapter, cancellationToken)),
            changedBlockIds);
    }

    private async Task<Dictionary<Guid, Guid>> CloneReferencedCompositionsAsync(
        Chapter chapter,
        PublicationEdition edition,
        ManuscriptDocument document,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var sourceIds = DesignedPageIds(document);
        if (sourceIds.Count == 0)
            return [];
        var sources = await db.PageCompositions.AsNoTracking()
            .Include(item => item.Variants.Where(variant => variant.DetachedAt == null))
            .Where(item => item.ProjectId == chapter.ProjectId
                && item.ChapterId == chapter.Id
                && item.EditionId == null
                && item.DetachedAt == null
                && sourceIds.Contains(item.Id))
            .ToListAsync(cancellationToken);
        if (sources.Count != sourceIds.Count)
            throw new InvalidDataException("Every inherited Designed Page must be owned by the Core chapter before it can be forked.");
        var remap = new Dictionary<Guid, Guid>();
        foreach (var source in sources)
        {
            var cloneId = Guid.NewGuid();
            var semantic = ManuscriptCodec.Deserialize(
                source.SemanticManuscriptJson,
                source.Id,
                source.Revision) with
            {
                ManuscriptId = cloneId,
            };
            var clone = new PageComposition
            {
                Id = cloneId,
                ProjectId = source.ProjectId,
                ChapterId = source.ChapterId,
                EditionId = edition.Id,
                SourceCompositionId = source.SourceCompositionId ?? source.Id,
                Name = source.Name,
                SemanticManuscriptJson = ManuscriptCodec.Serialize(semantic),
                Revision = source.Revision,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            var variantIdMap = new Dictionary<Guid, Guid>();
            foreach (var sourceVariant in source.Variants)
            {
                var variant = new PageCompositionVariant
                {
                    Id = Guid.NewGuid(),
                    Composition = clone,
                    CompositionId = clone.Id,
                    GeometryKey = sourceVariant.GeometryKey,
                    SceneJson = sourceVariant.SceneJson,
                    Revision = sourceVariant.Revision,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                };
                variantIdMap[sourceVariant.Id] = variant.Id;
                clone.Variants.Add(variant);
            }
            if (source.ActiveAuthoringVariantId is Guid activeId && variantIdMap.TryGetValue(activeId, out var clonedActiveId))
                clone.ActiveAuthoringVariantId = clonedActiveId;
            db.PageCompositions.Add(clone);
            remap[source.Id] = clone.Id;
        }
        return remap;
    }

    public async Task<Guid> EnsureEditionCompositionAsync(
        EditorContentTarget target,
        Guid chapterId,
        Guid sourceCompositionId,
        CancellationToken cancellationToken = default)
    {
        var projectId = await GetChapterProjectIdAsync(chapterId, cancellationToken);
        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        await contestGuard.EnsureMutationAllowedAsync(projectId, cancellationToken);
        var db = databaseOperation.Db;
        var repo = databaseOperation.Repositories.Chapters;
        if (target.EditionId is not Guid editionId)
            throw new InvalidOperationException("A Core Designed Page does not require an edition snapshot.");
        var chapter = await repo.GetByIdAsync(chapterId, cancellationToken)
            ?? throw new KeyNotFoundException("The chapter was not found.");
        var edition = await RequireEditableEditionAsync(target, chapter.ProjectId, cancellationToken);
        var existingComposition = await db.PageCompositions.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == sourceCompositionId && item.ProjectId == chapter.ProjectId
                && item.DetachedAt == null,
            cancellationToken) ?? throw new KeyNotFoundException("The Designed Page was not found.");
        if (existingComposition.EditionId == editionId)
            return existingComposition.Id;
        if (existingComposition.EditionId is not null || existingComposition.ChapterId != chapterId)
            throw new InvalidOperationException("The Designed Page does not belong to the selected chapter target.");

        var chapterOverride = await db.PublicationEditionChapterOverrides.SingleOrDefaultAsync(
            item => item.EditionId == editionId && item.ChapterId == chapterId,
            cancellationToken);
        if (chapterOverride is not null)
        {
            var existingClone = await db.PageCompositions.AsNoTracking().SingleOrDefaultAsync(
                item => item.ProjectId == chapter.ProjectId
                    && item.ChapterId == chapterId
                    && item.EditionId == editionId
                    && item.DetachedAt == null
                    && item.SourceCompositionId == sourceCompositionId,
                cancellationToken);
            return existingClone?.Id
                ?? throw new InvalidOperationException("The divergent edition chapter does not reference this Core Designed Page.");
        }

        var core = ManuscriptCodec.Deserialize(chapter.ManuscriptJson, chapter.Id, chapter.ManuscriptRevision);
        if (!DesignedPageIds(core).Contains(sourceCompositionId))
            throw new InvalidOperationException("The Core chapter does not reference this Designed Page.");
        var remap = await CloneReferencedCompositionsAsync(chapter, edition, core, cancellationToken);
        var document = RemapCompositions(core, remap);
        chapterOverride = new PublicationEditionChapterOverride
        {
            EditionId = edition.Id,
            ChapterId = chapter.Id,
            BaseCoreRevision = chapter.ManuscriptRevision,
            BaseCoreHash = ManuscriptCodec.HashPlainText(ManuscriptCodec.ProjectPlainText(core)),
            ManuscriptJson = ManuscriptCodec.Serialize(document),
            Revision = document.Revision,
        };
        db.PublicationEditionChapterOverrides.Add(chapterOverride);
        edition.Revision = checked(edition.Revision + 1);
        await annotations.RebaseForManuscriptMutationAsync(
            chapter.ProjectId, target, chapter.Id, document, document.Revision, cancellationToken);
        edition.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await databaseOperation.DisposeAsync();
        await TryReindexEditionBodyAsync(target, chapterId, cancellationToken);
        return remap[sourceCompositionId];
    }

    private static ManuscriptDocument RemapCompositions(ManuscriptDocument document, IReadOnlyDictionary<Guid, Guid> remap) =>
        remap.Count == 0
            ? document
            : document with
            {
                Content = document.Content.Select(block =>
                    block.PageCompositionId is Guid sourceId && remap.TryGetValue(sourceId, out var cloneId)
                        ? block with { PageCompositionId = cloneId }
                        : block).ToList(),
            };

    private async Task<EditionManuscriptState> GetRequiredEditionStateAsync(
        EditorContentTarget target,
        Chapter chapter,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var edition = await RequireEditableEditionAsync(target, chapter.ProjectId, cancellationToken);
        var chapterOverride = await db.PublicationEditionChapterOverrides
            .SingleOrDefaultAsync(item => item.EditionId == edition.Id && item.ChapterId == chapter.Id, cancellationToken);
        var document = chapterOverride is null
            ? ManuscriptCodec.Deserialize(chapter.ManuscriptJson, chapter.Id, chapter.ManuscriptRevision)
            : ManuscriptCodec.Deserialize(chapterOverride.ManuscriptJson, chapter.Id, chapterOverride.Revision);
        if (document.Revision != expectedRevision)
            throw new ManuscriptRevisionConflictException(expectedRevision, document.Revision);
        var snapshot = chapterOverride is null
            ? await SnapshotAsync(chapter, cancellationToken)
            : await EditionSnapshotAsync(target, chapter, cancellationToken);
        return new EditionManuscriptState(chapterOverride, document, snapshot);
    }

    private async Task<EditionManuscriptState> GetRequiredEditionStateWithoutRevisionAsync(
        EditorContentTarget target,
        Chapter chapter,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var edition = await RequireEditableEditionAsync(target, chapter.ProjectId, cancellationToken);
        var chapterOverride = await db.PublicationEditionChapterOverrides
            .SingleOrDefaultAsync(item => item.EditionId == edition.Id && item.ChapterId == chapter.Id, cancellationToken);
        var document = chapterOverride is null
            ? ManuscriptCodec.Deserialize(chapter.ManuscriptJson, chapter.Id, chapter.ManuscriptRevision)
            : ManuscriptCodec.Deserialize(chapterOverride.ManuscriptJson, chapter.Id, chapterOverride.Revision);
        var snapshot = chapterOverride is null
            ? await SnapshotAsync(chapter, cancellationToken)
            : await EditionSnapshotAsync(target, chapter, cancellationToken);
        return new EditionManuscriptState(chapterOverride, document, snapshot);
    }

    private async Task<PublicationEdition> RequireEditableEditionAsync(
        EditorContentTarget target,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        if (target.EditionId is not Guid editionId)
            throw new InvalidOperationException("An edition content target requires an edition ID.");
        var edition = await db.PublicationEditions.SingleOrDefaultAsync(
            item => item.Id == editionId && item.ProjectId == projectId,
            cancellationToken) ?? throw new KeyNotFoundException("The selected publication release was not found in this project.");
        if (edition.Status == PublicationEditionStatus.Archived)
            throw new InvalidOperationException("Archived releases cannot edit edition-specific content.");
        if (!edition.EditionSpecificContentEnabled)
            throw new InvalidOperationException("Edition-specific content is not enabled for this release.");
        return edition;
    }

    private static bool EquivalentContent(ManuscriptDocument left, ManuscriptDocument right) =>
        string.Equals(
            ManuscriptCodec.Serialize(left with { Revision = 0 }),
            ManuscriptCodec.Serialize(right with { Revision = 0 }),
            StringComparison.Ordinal);

    private sealed record EditionManuscriptState(
        PublicationEditionChapterOverride? Override,
        ManuscriptDocument Document,
        ManuscriptSnapshot Snapshot);


    private async Task TryReindexBodyAsync(Guid chapterId, CancellationToken cancellationToken)
    {
        try
        {
            await indexWork.QueueOrRunAsync(
                VectorIndexWorkKind.ChapterBody,
                chapterId.ToString("N"),
                ct => ReindexAsync(chapterId, ct),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // ReindexAsync already records failure details on the chapter.
        }
    }

    private async Task TryReindexEditionBodyAsync(
        EditorContentTarget target,
        Guid chapterId,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        if (target.EditionId is not Guid editionId)
            return;
        var chapterOverride = await db.PublicationEditionChapterOverrides.AsNoTracking()
            .SingleOrDefaultAsync(item => item.EditionId == editionId && item.ChapterId == chapterId, cancellationToken);
        if (chapterOverride is null)
            return;
        try
        {
            await indexWork.QueueOrRunAsync(
                VectorIndexWorkKind.EditionChapter,
                chapterOverride.Id.ToString("N"),
                ct => ReindexEditionAsync(chapterOverride.Id, ct),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to index edition chapter {EditionId}/{ChapterId}.", editionId, chapterId);
        }
    }

    private async Task ReindexEditionAsync(Guid overrideId, CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var chapterOverride = await db.PublicationEditionChapterOverrides.AsNoTracking()
            .Include(item => item.Chapter)
            .Include(item => item.Edition)
            .SingleOrDefaultAsync(item => item.Id == overrideId, cancellationToken);
        if (chapterOverride is null)
            return;
        var chapter = chapterOverride.Chapter;
        var document = ManuscriptCodec.Deserialize(chapterOverride.ManuscriptJson, chapter.Id, chapterOverride.Revision);
        var text = ManuscriptCodec.ProjectPlainText(document);
        var scopeKey = Project.ScopeKey(chapter.ProjectId);
        var sourceId = chapterOverride.Id.ToString("N");
        await projectSearch.DeleteBySourceAsync(ProjectSearchSourceTypes.EditionChapter, sourceId, scopeKey, cancellationToken);
        await vectors.DeleteBySourceAsync(ProjectSearchSourceTypes.EditionChapter, sourceId, scopeKey, cancellationToken);
        var searchText = BuildChapterSearchText(chapter, text);
        var searchChunks = chunker.Chunk(searchText);
        await projectSearch.StoreManyAsync(searchChunks.Select(chunk => new ProjectSearchIndexChunk(
            chunk.Content,
            ProjectSearchSourceTypes.EditionChapter,
            scopeKey,
            sourceId,
            chapter.Id.ToString("N"),
            $"{chapterOverride.Edition.Name}: {chapter.Title}",
            $"Edition chapter {chapter.Order + 1} — {chapter.Title}",
            chunk.Index)), cancellationToken);
        if (!await embeddings.IsAvailableAsync(cancellationToken))
            return;
        var chunks = chunker.Chunk(text);
        if (chunks.Count == 0)
            return;
        var embeddingsList = await embeddings.GenerateEmbeddingsAsync(chunks.Select(item => item.Content).ToList(), cancellationToken);
        for (var index = 0; index < chunks.Count; index++)
        {
            await vectors.StoreAsync(
                chunks[index].Content,
                embeddingsList[index],
                ProjectSearchSourceTypes.EditionChapter,
                scopeKey,
                sourceId,
                $"{chapterOverride.Edition.Name} — {chapter.Title} — Part {index + 1}/{chunks.Count}",
                index,
                cancellationToken);
        }
    }

    public async Task DeleteAsync(Guid chapterId, CancellationToken cancellationToken = default)
    {
        Chapter? chapter;
        await using (var readOperation = await database.OpenReadAsync(cancellationToken))
            chapter = await readOperation.Repositories.Chapters.GetByIdAsync(chapterId, cancellationToken);
        if (chapter is null) return;
        var projectId = chapter.ProjectId;
        await contestGuard.EnsureMutationAllowedAsync(projectId, cancellationToken);
        var actId = chapter.ActId;
        await using var mutation = await projectMutations.AcquireAsync(projectId, cancellationToken);

        try
        {
            await vectors.DeleteBySourceAsync("chapter", chapter.VectorSourceId, Project.ScopeKey(chapter.ProjectId), cancellationToken);
        }
        catch (Exception ex)
        {
            // Don't block the EF delete; vector orphans are tolerable, missing rows are not.
            logger.LogWarning(ex, "Failed to delete vector chunks for chapter {ChapterId}", chapter.Id);
        }

        // Wipe the chapter's graph footprint (its lazily-upserted Chapter node + any HasChild
        // beat children) so the entity layer doesn't leak orphans. Tolerate failures.
        try
        {
            await outlineGraphSync.RemoveChapterAsync(chapter.ProjectId, chapter.Id, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to clean graph entries for chapter {ChapterId}", chapter.Id);
        }

        await contextIndexing.DeleteChapterAsync(projectId, chapter.Id, cancellationToken);

        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var ownedCompositions = await db.PageCompositions.IgnoreQueryFilters().AsNoTracking()
            .Where(item => item.ProjectId == projectId && item.ChapterId == chapter.Id)
            .Select(item => item.Id)
            .ToListAsync(cancellationToken);
        var editionIds = await db.PublicationEditionChapterOverrides.AsNoTracking()
            .Where(item => item.ChapterId == chapter.Id)
            .Select(item => item.EditionId)
            .ToListAsync(cancellationToken);
        databaseOperation.Repositories.Chapters.Remove(chapter);
        await databaseOperation.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await transaction.DisposeAsync();
        await databaseOperation.DisposeAsync();
        await authoringHistory.DeleteDocumentHistoryAsync(
            projectId,
            AuthoringHistoryDocumentKind.CoreChapter,
            chapter.Id,
            cancellationToken: CancellationToken.None);
        foreach (var editionId in editionIds)
        {
            await authoringHistory.DeleteDocumentHistoryAsync(
                projectId,
                AuthoringHistoryDocumentKind.EditionChapter,
                chapter.Id,
                editionId,
                CancellationToken.None);
        }
        foreach (var compositionId in ownedCompositions)
        {
            await authoringHistory.DeleteDocumentHistoryAsync(
                projectId,
                AuthoringHistoryDocumentKind.PageComposition,
                compositionId,
                cancellationToken: CancellationToken.None);
        }
        if (actId is Guid deletedFromActId)
            await contextIndexing.ReindexActAsync(deletedFromActId, cancellationToken);
    }

    public async Task ReorderAsync(Guid projectId, Guid? actId, IReadOnlyList<Guid> orderedIds, CancellationToken cancellationToken = default)
    {
        await contestGuard.EnsureMutationAllowedAsync(projectId, cancellationToken);
        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var repo = databaseOperation.Repositories.Chapters;
        var projects = databaseOperation.Repositories.Projects;
        await repo.ReorderAsync(projectId, actId, orderedIds, cancellationToken);

        var project = await projects.GetByIdAsync(projectId, cancellationToken);
        if (project is not null)
        {
            project.UpdatedAt = DateTime.UtcNow;
            projects.Update(project);
        }

        await databaseOperation.SaveChangesAsync(cancellationToken);
        await databaseOperation.DisposeAsync();
        await outlineGraphSync.RepairProjectAsync(projectId, cancellationToken);
        foreach (var chapterId in orderedIds)
            await contextIndexing.ReindexChapterAsync(chapterId, cancellationToken);
        if (actId is Guid reorderedActId)
            await contextIndexing.ReindexActAsync(reorderedActId, cancellationToken);
    }

    public async Task ReindexAsync(Guid chapterId, CancellationToken cancellationToken = default)
    {
        Chapter? chapter;
        await using (var readOperation = await database.OpenReadAsync(cancellationToken))
            chapter = await readOperation.Repositories.Chapters.GetByIdAsync(chapterId, cancellationToken);
        if (chapter is null) return;

        var scopeKey = Project.ScopeKey(chapter.ProjectId);
        var sourceId = chapter.VectorSourceId;

        try
        {
            await vectors.DeleteBySourceAsync("chapter", sourceId, scopeKey, cancellationToken);
            await projectSearch.DeleteBySourceAsync(ProjectSearchSourceTypes.Chapter, sourceId, scopeKey, cancellationToken);

            var expandedText = await semanticProjection.ExpandPlainTextAsync(chapter, cancellationToken);
            var searchText = BuildChapterSearchText(chapter, expandedText);
            var searchChunks = chunker.Chunk(searchText);
            await projectSearch.StoreManyAsync(searchChunks.Select(chunk => new ProjectSearchIndexChunk(
                chunk.Content,
                ProjectSearchSourceTypes.Chapter,
                scopeKey,
                sourceId,
                null,
                $"Chapter {chapter.Order + 1} {chapter.Title}",
                $"Chapter {chapter.Order + 1} - {chapter.Title}",
                chunk.Index)), cancellationToken);

            if (!await embeddings.IsAvailableAsync(cancellationToken))
            {
                chapter.VectorIndexState = VectorIndexState.Disabled;
                chapter.VectorIndexedAt = null;
                chapter.VectorIndexError = null;
                await UpdateVectorIndexStateAsync(chapter, cancellationToken);
                await outlineGraphSync.EnsureChapterAsync(chapter, cancellationToken);
                await autoLinks.RefreshSourceAsync(chapter.ProjectId, ProjectSearchSourceTypes.Chapter, chapter.Id, cancellationToken);
                await contextIndexing.ReindexChapterAsync(chapter.Id, cancellationToken);
                logger.LogDebug("Skipped chapter vector indexing for {ChapterId}; no embedding model is configured.", chapter.Id);
                return;
            }

            var chunks = chunker.Chunk(expandedText);
            if (chunks.Count > 0)
            {
                var contents = chunks.Select(c => c.Content).ToList();
                var vectorsList = await embeddings.GenerateEmbeddingsAsync(contents, cancellationToken);

                var total = chunks.Count;
                for (var i = 0; i < total; i++)
                {
                    var metadata = $"Chapter {chapter.Order + 1} • Part {i + 1}/{total} • {chapter.Title}";
                    await vectors.StoreAsync(
                        content: chunks[i].Content,
                        embedding: vectorsList[i],
                        sourceType: "chapter",
                        scopeKey: scopeKey,
                        sourceId: sourceId,
                        metadata: metadata,
                        chunkIndex: i,
                        cancellationToken: cancellationToken);
                }
            }

            chapter.VectorIndexState = VectorIndexState.UpToDate;
            chapter.VectorIndexedAt = DateTime.UtcNow;
            chapter.VectorIndexError = null;
            await UpdateVectorIndexStateAsync(chapter, cancellationToken);
            await outlineGraphSync.EnsureChapterAsync(chapter, cancellationToken);
            await autoLinks.RefreshSourceAsync(chapter.ProjectId, ProjectSearchSourceTypes.Chapter, chapter.Id, cancellationToken);
            await contextIndexing.ReindexChapterAsync(chapter.Id, cancellationToken);

            logger.LogDebug("Reindexed chapter {ChapterId} with {Count} chunks", chapter.Id, chunks.Count);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to reindex chapter {ChapterId}", chapter.Id);
            chapter.VectorIndexState = VectorIndexState.Failed;
            chapter.VectorIndexError = ex.Message;
            try { await UpdateVectorIndexStateAsync(chapter, cancellationToken); }
            catch (Exception saveEx) { logger.LogError(saveEx, "Failed to persist reindex failure for chapter {ChapterId}", chapter.Id); }
            throw;
        }
    }

    private async Task<Guid> GetChapterProjectIdAsync(Guid chapterId, CancellationToken cancellationToken)
    {
        await using var operation = await database.OpenReadAsync(cancellationToken);
        var projectId = await operation.Db.Chapters.AsNoTracking()
            .Where(item => item.Id == chapterId)
            .Select(item => item.ProjectId)
            .SingleOrDefaultAsync(cancellationToken);
        return projectId != Guid.Empty
            ? projectId
            : throw new KeyNotFoundException("The chapter was not found.");
    }

    private async Task UpdateVectorIndexStateAsync(Chapter chapter, CancellationToken cancellationToken)
    {
        await using var operation = await database.OpenWriteAsync(chapter.ProjectId, cancellationToken);
        var stored = await operation.Repositories.Chapters.GetByIdAsync(chapter.Id, cancellationToken);
        if (stored is null)
            return;

        stored.VectorIndexState = chapter.VectorIndexState;
        stored.VectorIndexedAt = chapter.VectorIndexedAt;
        stored.VectorIndexError = chapter.VectorIndexError;
        operation.Repositories.Chapters.Update(stored);
        await operation.SaveChangesAsync(cancellationToken);
    }

    private static string BuildChapterSearchText(Chapter chapter, string expandedText)
    {
        var parts = new List<string>
        {
            $"Type: Chapter",
            $"Title: {chapter.Title}",
        };
        if (!string.IsNullOrWhiteSpace(chapter.Synopsis))
            parts.Add($"Synopsis: {chapter.Synopsis}");
        if (!string.IsNullOrWhiteSpace(expandedText))
            parts.Add($"Body: {expandedText}");
        return string.Join('\n', parts);
    }
}
