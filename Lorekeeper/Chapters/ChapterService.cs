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
    IVectorStore vectors, IEmbeddingService embeddings, ITextChunker chunker, IProjectSearchIndex projectSearch, IGraphAutoLinkService autoLinks, IOutlineGraphSync outlineGraphSync, IContextIndexingService contextIndexing, IVectorIndexWorkCoordinator indexWork, IAppDatabaseOperationFactory database, IManuscriptStyleService manuscriptStyles, IChapterSemanticProjectionService semanticProjection, IProjectMutationCoordinator projectMutations, IAuthoringMutationContextAccessor authoringMutationContext, IAuthoringGenerationService authoringGenerations, IManuscriptAnnotationService annotations, IEditorContestMutationGuard contestGuard, IEditorContestMutationContext contestMutationContext, ILogger<ChapterService> logger) : IChapterService, IManuscriptService
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

        if (!contestMutationContext.IsAuthorized(persisted.Chapter.ProjectId)
            && !authoringMutationContext.IsHistorySuppressed)
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

        await RefreshPlacementReferencesAsync(
            db,
            chapter.ProjectId,
            DesignedPageContainerKind.Chapter,
            chapter.Id,
            null,
            document,
            cancellationToken);

        var invalidateTargets = authoringMutationContext.IsHistorySuppressed
            ? []
            : new[] { $"chapter:{chapter.Id:D}" };

        if (invalidateTargets.Length > 0)
        {
            await authoringGenerations.StageInvalidationAsync(
                db,
                chapter.ProjectId,
                invalidateTargets,
                cancellationToken);
        }

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
        authoringGenerations.CompleteInvalidation(invalidateTargets);

        return (new ManuscriptMutationResult(await SnapshotAsync(chapter, cancellationToken), changedBlockIds), chapter);
    }

    private async Task ValidateFigureAssetsAsync(
        Guid projectId,
        ManuscriptDocument document,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var imageIds = ManuscriptTraversal.EnumerateBlocks(document)
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
        if (ids.Count == 0)
            return;
        var pages = await db.DesignedPages.AsNoTracking()
            .Include(page => page.Contents)
            .Where(page => page.ProjectId == chapter.ProjectId && ids.Contains(page.Id))
            .ToListAsync(cancellationToken);
        if (pages.Count != ids.Count)
            throw new InvalidDataException("Every Designed Page must reference a page in this project.");
        if (pages.Any(page => page.ScopeEditionId is Guid scopeId && scopeId != target.EditionId))
            throw new InvalidDataException("A Designed Page is outside the selected release scope.");
        if (pages.Any(page => !page.Contents.Any(content => content.EditionId == target.EditionId)
            && !page.Contents.Any(content => content.EditionId == null)))
            throw new InvalidDataException("A Designed Page has no effective content for this target.");
    }

    private static async Task RefreshPlacementReferencesAsync(
        AppDbContext db,
        Guid projectId,
        DesignedPageContainerKind containerKind,
        Guid containerId,
        Guid? editionId,
        ManuscriptDocument? document,
        CancellationToken cancellationToken)
    {
        var desired = (document?.Content ?? [])
            .Where(block => block.Type == ManuscriptBlockType.DesignedPage)
            .ToList();
        if (desired.GroupBy(block => block.Id, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new InvalidDataException("Designed Page placement IDs must be unique within the manuscript.");

        var desiredIds = desired.Select(block => block.Id).ToHashSet(StringComparer.Ordinal);
        var current = await db.DesignedPagePlacementReferences
            .Where(item => item.ProjectId == projectId
                && item.ContainerKind == containerKind
                && item.ContainerId == containerId
                && item.EditionId == editionId)
            .ToListAsync(cancellationToken);
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
                    ProjectId = projectId,
                    CreatedAt = now,
                };
                db.DesignedPagePlacementReferences.Add(reference);
                current.Add(reference);
            }
            reference.DesignedPageId = block.DesignedPageId!.Value;
            reference.ContainerKind = containerKind;
            reference.ContainerId = containerId;
            reference.EditionId = editionId;
            reference.ManuscriptRevision = document!.Revision;
            reference.UpdatedAt = now;
        }
    }

    private static HashSet<Guid> DesignedPageIds(ManuscriptDocument document) =>
        document.Content
            .Where(block => block.Type == ManuscriptBlockType.DesignedPage)
            .Select(block => block.DesignedPageId!.Value)
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
        if (target.EditionId is not Guid editionId)
            throw new InvalidOperationException("An edition content target requires an edition ID.");
        _ = await db.PublicationEditions.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == editionId && item.ProjectId == chapter.ProjectId,
            cancellationToken) ?? throw new KeyNotFoundException("The selected publication release was not found in this project.");
        var chapterOverride = await db.PublicationEditionChapterOverrides.AsNoTracking()
            .SingleOrDefaultAsync(item => item.EditionId == editionId && item.ChapterId == chapter.Id, cancellationToken);
        var document = chapterOverride is null
            ? ManuscriptCodec.Deserialize(chapter.ManuscriptJson, chapter.Id, chapter.ManuscriptRevision)
            : ManuscriptCodec.Deserialize(chapterOverride.ManuscriptJson, chapter.Id, chapterOverride.Revision);
        var plainText = await semanticProjection.ExpandPlainTextAsync(
            chapter.ProjectId, document, target, cancellationToken);
        return new ManuscriptSnapshot(
            chapter.Id,
            document.Revision,
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
        if (chapterOverride is null)
        {
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
        chapterOverride.ManuscriptJson = ManuscriptCodec.Serialize(document);
        chapterOverride.Revision = document.Revision;
        chapterOverride.UpdatedAt = DateTime.UtcNow;
        edition.UpdatedAt = DateTime.UtcNow;
        edition.Revision = checked(edition.Revision + 1);
        await annotations.RebaseForManuscriptMutationAsync(
            chapter.ProjectId, target, chapter.Id, document, document.Revision, cancellationToken);
        await RefreshPlacementReferencesAsync(
            db,
            chapter.ProjectId,
            DesignedPageContainerKind.Chapter,
            chapter.Id,
            edition.Id,
            document,
            cancellationToken);
        var invalidateTargets = authoringMutationContext.IsHistorySuppressed
            ? []
            : new[] { $"release:{edition.Id:D}:chapter:{chapter.Id:D}" };

        if (invalidateTargets.Length > 0)
        {
            await authoringGenerations.StageInvalidationAsync(
                db,
                chapter.ProjectId,
                invalidateTargets,
                cancellationToken);
        }

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
        authoringGenerations.CompleteInvalidation(invalidateTargets);

        await databaseOperation.DisposeAsync();
        if (!contestMutationContext.IsAuthorized(chapter.ProjectId) && !authoringMutationContext.IsHistorySuppressed)
            await TryReindexEditionBodyAsync(target, chapter.Id, cancellationToken);
        return new ManuscriptMutationResult(
            (await EditionSnapshotAsync(target, chapter, cancellationToken)),
            changedBlockIds);
    }

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
        try
        {
            await indexWork.QueueOrRunAsync(
                VectorIndexWorkKind.EditionChapter,
                $"{editionId:N}:{chapterId:N}",
                ct => ReindexEditionAsync(editionId, chapterId, ct),
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

    private async Task ReindexEditionAsync(Guid editionId, Guid chapterId, CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var edition = await db.PublicationEditions.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == editionId, cancellationToken);
        var chapter = await db.Chapters.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == chapterId, cancellationToken);
        if (edition is null || chapter is null || edition.ProjectId != chapter.ProjectId)
            return;
        var chapterOverride = await db.PublicationEditionChapterOverrides.AsNoTracking()
            .SingleOrDefaultAsync(item => item.EditionId == editionId && item.ChapterId == chapterId, cancellationToken);
        var document = chapterOverride is null
            ? ManuscriptCodec.Deserialize(chapter.ManuscriptJson, chapter.Id, chapter.ManuscriptRevision)
            : ManuscriptCodec.Deserialize(chapterOverride.ManuscriptJson, chapter.Id, chapterOverride.Revision);
        var text = await semanticProjection.ExpandPlainTextAsync(
            chapter.ProjectId, document, EditorContentTarget.ForEdition(editionId), cancellationToken);
        var scopeKey = Project.ScopeKey(chapter.ProjectId);
        var sourceId = $"{editionId:N}:{chapterId:N}";
        if (chapterOverride is not null)
        {
            var legacySourceId = chapterOverride.Id.ToString("N");
            await projectSearch.DeleteBySourceAsync(ProjectSearchSourceTypes.EditionChapter, legacySourceId, scopeKey, cancellationToken);
            await vectors.DeleteBySourceAsync(ProjectSearchSourceTypes.EditionChapter, legacySourceId, scopeKey, cancellationToken);
        }
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
            $"{edition.Name}: {chapter.Title}",
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
                $"{edition.Name} — {chapter.Title} — Part {index + 1}/{chunks.Count}",
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
        var editionIds = await db.PublicationEditionChapterOverrides.AsNoTracking()
            .Where(item => item.ChapterId == chapter.Id)
            .Select(item => item.EditionId)
            .ToListAsync(cancellationToken);
        var placementReferences = await db.DesignedPagePlacementReferences
            .Where(item => item.ProjectId == projectId
                && item.ContainerKind == DesignedPageContainerKind.Chapter
                && item.ContainerId == chapter.Id)
            .ToListAsync(cancellationToken);
        db.DesignedPagePlacementReferences.RemoveRange(placementReferences);
        databaseOperation.Repositories.Chapters.Remove(chapter);
        await databaseOperation.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await transaction.DisposeAsync();
        await databaseOperation.DisposeAsync();
        authoringGenerations.CompleteInvalidation([$"chapter:{chapter.Id:D}"]);
        foreach (var editionId in editionIds)
        {
            authoringGenerations.CompleteInvalidation(
                [$"release:{editionId:D}:chapter:{chapter.Id:D}"]);
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
