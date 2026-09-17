using Lorekeeper.Composition;
using Lorekeeper.EditorChat;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Authoring;

public interface IAuthoringCompoundManuscriptRestoreService
{
    Task ApplyAsync(
        IReadOnlyList<AuthoringHistorySnapshotRestore> restores,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Restores the manuscript-only members of one multi-target history action in
/// a single project transaction. Designed Page content has its own history
/// stream and is intentionally outside this boundary.
/// </summary>
public sealed class AuthoringCompoundManuscriptRestoreService(
    IAppDatabaseOperationFactory database,
    IManuscriptAnnotationService annotations,
    IEditorContestMutationGuard contestGuard) : IAuthoringCompoundManuscriptRestoreService
{
    public async Task ApplyAsync(
        IReadOnlyList<AuthoringHistorySnapshotRestore> restores,
        CancellationToken cancellationToken = default)
    {
        if (restores.Count == 0)
            throw new ArgumentException("At least one manuscript snapshot is required.", nameof(restores));
        var projectId = restores[0].Target.ProjectId;
        if (restores.Any(item => item.Target.ProjectId != projectId))
            throw new InvalidOperationException("A compound manuscript restore cannot span projects.");
        if (restores.Any(item => item.Target.Kind is
                AuthoringHistoryDocumentKind.CoreChapter or AuthoringHistoryDocumentKind.EditionChapter))
        {
            await contestGuard.EnsureMutationAllowedAsync(projectId, cancellationToken);
        }

        await using var operation = await database.OpenWriteAsync(projectId, cancellationToken);
        operation.ShareWithNestedOperations();
        var db = operation.Db;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var prepared = new List<PreparedRestore>(restores.Count);
        foreach (var restore in restores.OrderBy(item => item.Target.RegistryKey, StringComparer.Ordinal))
        {
            var current = await CaptureCurrentAsync(db, restore.Target, cancellationToken);
            if (!string.Equals(current, restore.ExpectedCurrentSnapshot, StringComparison.Ordinal))
                throw new InvalidOperationException("A manuscript in this compound Undo action changed before it could be restored.");
            prepared.Add(await PrepareAsync(db, restore, cancellationToken));
        }

        foreach (var item in prepared)
            await ApplyPreparedAsync(db, item, cancellationToken);

        var project = await db.Projects.SingleOrDefaultAsync(item => item.Id == projectId, cancellationToken)
            ?? throw new KeyNotFoundException("The project for this compound Undo action was not found.");
        project.UpdatedAt = DateTime.UtcNow;
        foreach (var editionId in restores.Select(item => item.Target.EditionId).OfType<Guid>().Distinct())
        {
            var edition = await db.PublicationEditions.SingleAsync(
                item => item.ProjectId == projectId && item.Id == editionId,
                cancellationToken);
            edition.Revision = checked(edition.Revision + 1);
            edition.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<string> CaptureCurrentAsync(
        AppDbContext db,
        AuthoringHistoryTarget target,
        CancellationToken cancellationToken)
    {
        ManuscriptDocument document;
        var inherited = false;
        switch (target.Kind)
        {
            case AuthoringHistoryDocumentKind.CoreChapter:
            {
                var chapter = await db.Chapters.AsNoTracking().SingleOrDefaultAsync(
                    item => item.ProjectId == target.ProjectId && item.Id == target.DocumentId,
                    cancellationToken) ?? throw new KeyNotFoundException("A chapter in this compound Undo action was not found.");
                document = ManuscriptCodec.Deserialize(chapter.ManuscriptJson, chapter.Id, chapter.ManuscriptRevision);
                break;
            }
            case AuthoringHistoryDocumentKind.EditionChapter when target.EditionId is Guid editionId:
            {
                var chapter = await db.Chapters.AsNoTracking().SingleOrDefaultAsync(
                    item => item.ProjectId == target.ProjectId && item.Id == target.DocumentId,
                    cancellationToken) ?? throw new KeyNotFoundException("A release chapter in this compound Undo action was not found.");
                var chapterOverride = await db.PublicationEditionChapterOverrides.AsNoTracking().SingleOrDefaultAsync(
                    item => item.EditionId == editionId && item.ChapterId == chapter.Id,
                    cancellationToken);
                inherited = chapterOverride is null;
                document = chapterOverride is null
                    ? ManuscriptCodec.Deserialize(chapter.ManuscriptJson, chapter.Id, chapter.ManuscriptRevision)
                    : ManuscriptCodec.Deserialize(chapterOverride.ManuscriptJson, chapter.Id, chapterOverride.Revision);
                break;
            }
            case AuthoringHistoryDocumentKind.PublicationSection:
            {
                var section = await db.PublicationSections.AsNoTracking().SingleOrDefaultAsync(
                    item => item.ProjectId == target.ProjectId
                        && item.Id == target.DocumentId
                        && item.EditionId == target.EditionId,
                    cancellationToken) ?? throw new KeyNotFoundException("A publication section in this compound Undo action was not found.");
                document = ManuscriptCodec.Deserialize(section.ManuscriptJson, section.Id, section.Revision);
                break;
            }
            default:
                throw new InvalidOperationException("Only chapter and publication-section histories may join a compound manuscript action.");
        }

        return await AuthoringSnapshotCodec.CaptureManuscriptAsync(
            db,
            document,
            target.ProjectId,
            target.Kind is AuthoringHistoryDocumentKind.CoreChapter or AuthoringHistoryDocumentKind.EditionChapter
                ? target.DocumentId
                : null,
            target.Kind == AuthoringHistoryDocumentKind.PublicationSection ? target.DocumentId : null,
            target.EditionId,
            cancellationToken,
            inherited);
    }

    private static async Task<PreparedRestore> PrepareAsync(
        AppDbContext db,
        AuthoringHistorySnapshotRestore restore,
        CancellationToken cancellationToken)
    {
        var saved = AuthoringSnapshotCodec.ReadManuscript(restore.Snapshot);
        var source = ManuscriptCodec.Deserialize(saved.ManuscriptJson);
        if (restore.Target.EditionId is Guid editionId)
        {
            var edition = await db.PublicationEditions.AsNoTracking().SingleOrDefaultAsync(
                item => item.ProjectId == restore.Target.ProjectId && item.Id == editionId,
                cancellationToken) ?? throw new KeyNotFoundException("The release for this compound Undo action was not found.");
            if (edition.Status == PublicationEditionStatus.Archived)
                throw new InvalidOperationException("Archived release content is immutable.");
        }
        return new PreparedRestore(restore.Target, saved.Inherited, source);
    }

    private async Task ApplyPreparedAsync(
        AppDbContext db,
        PreparedRestore restore,
        CancellationToken cancellationToken)
    {
        switch (restore.Target.Kind)
        {
            case AuthoringHistoryDocumentKind.CoreChapter:
            {
                var chapter = await db.Chapters.SingleAsync(
                    item => item.ProjectId == restore.Target.ProjectId && item.Id == restore.Target.DocumentId,
                    cancellationToken);
                var document = restore.Document with
                {
                    ManuscriptId = chapter.Id,
                    Revision = checked(chapter.ManuscriptRevision + 1),
                };
                await ValidateDesignedPagesAsync(db, restore.Target, document, cancellationToken);
                chapter.ManuscriptJson = ManuscriptCodec.Serialize(document);
                chapter.ManuscriptRevision = document.Revision;
                chapter.VectorIndexState = VectorIndexState.Stale;
                chapter.UpdatedAt = DateTime.UtcNow;
                await SyncPlacementsAsync(db, restore.Target, document, cancellationToken);
                await annotations.RebaseForManuscriptMutationAsync(
                    restore.Target.ProjectId,
                    EditorContentTarget.Core,
                    chapter.Id,
                    document,
                    document.Revision,
                    cancellationToken);
                break;
            }
            case AuthoringHistoryDocumentKind.EditionChapter when restore.Target.EditionId is Guid editionId:
            {
                var chapter = await db.Chapters.SingleAsync(
                    item => item.ProjectId == restore.Target.ProjectId && item.Id == restore.Target.DocumentId,
                    cancellationToken);
                var chapterOverride = await db.PublicationEditionChapterOverrides.SingleOrDefaultAsync(
                    item => item.EditionId == editionId && item.ChapterId == chapter.Id,
                    cancellationToken);
                if (restore.Inherited)
                {
                    if (chapterOverride is not null)
                        db.PublicationEditionChapterOverrides.Remove(chapterOverride);
                    await SyncPlacementsAsync(db, restore.Target, null, cancellationToken);
                    var inherited = ManuscriptCodec.Deserialize(chapter.ManuscriptJson, chapter.Id, chapter.ManuscriptRevision);
                    await annotations.RebaseForManuscriptMutationAsync(
                        restore.Target.ProjectId,
                        EditorContentTarget.ForEdition(editionId),
                        chapter.Id,
                        inherited,
                        inherited.Revision,
                        cancellationToken);
                    break;
                }

                var nextRevision = checked((chapterOverride?.Revision ?? chapter.ManuscriptRevision) + 1);
                var document = restore.Document with { ManuscriptId = chapter.Id, Revision = nextRevision };
                await ValidateDesignedPagesAsync(db, restore.Target, document, cancellationToken);
                chapterOverride ??= new PublicationEditionChapterOverride
                {
                    EditionId = editionId,
                    ChapterId = chapter.Id,
                    BaseCoreRevision = chapter.ManuscriptRevision,
                    BaseCoreHash = ManuscriptCodec.HashPlainText(ManuscriptCodec.ProjectPlainText(chapter.Manuscript)),
                };
                if (db.Entry(chapterOverride).State == EntityState.Detached)
                    db.PublicationEditionChapterOverrides.Add(chapterOverride);
                chapterOverride.ManuscriptJson = ManuscriptCodec.Serialize(document);
                chapterOverride.Revision = document.Revision;
                chapterOverride.UpdatedAt = DateTime.UtcNow;
                await SyncPlacementsAsync(db, restore.Target, document, cancellationToken);
                await annotations.RebaseForManuscriptMutationAsync(
                    restore.Target.ProjectId,
                    EditorContentTarget.ForEdition(editionId),
                    chapter.Id,
                    document,
                    document.Revision,
                    cancellationToken);
                break;
            }
            case AuthoringHistoryDocumentKind.PublicationSection:
            {
                var section = await db.PublicationSections.SingleAsync(
                    item => item.ProjectId == restore.Target.ProjectId
                        && item.Id == restore.Target.DocumentId
                        && item.EditionId == restore.Target.EditionId,
                    cancellationToken);
                var document = restore.Document with
                {
                    ManuscriptId = section.Id,
                    Revision = checked(section.Revision + 1),
                };
                await ValidateDesignedPagesAsync(db, restore.Target, document, cancellationToken);
                section.ManuscriptJson = ManuscriptCodec.Serialize(document);
                section.Revision = document.Revision;
                section.UpdatedAt = DateTime.UtcNow;
                await SyncPlacementsAsync(db, restore.Target, document, cancellationToken);
                break;
            }
            default:
                throw new InvalidOperationException("Unsupported compound manuscript history target.");
        }
    }

    private static async Task ValidateDesignedPagesAsync(
        AppDbContext db,
        AuthoringHistoryTarget target,
        ManuscriptDocument document,
        CancellationToken cancellationToken)
    {
        ManuscriptCodec.Validate(document, target.DocumentId, document.Revision);
        foreach (var pageId in document.Content
                     .Where(item => item.Type == ManuscriptBlockType.DesignedPage)
                     .Select(item => item.DesignedPageId!.Value)
                     .Distinct())
        {
            var valid = await db.DesignedPages.AsNoTracking().AnyAsync(
                page => page.ProjectId == target.ProjectId
                    && page.Id == pageId
                    && (page.ScopeEditionId == null || page.ScopeEditionId == target.EditionId)
                    && page.Contents.Any(content => content.EditionId == target.EditionId || content.EditionId == null),
                cancellationToken);
            if (!valid)
                throw new InvalidDataException($"The compound Undo action references unavailable Designed Page {pageId:N}.");
        }
    }

    private static async Task SyncPlacementsAsync(
        AppDbContext db,
        AuthoringHistoryTarget target,
        ManuscriptDocument? document,
        CancellationToken cancellationToken)
    {
        var containerKind = target.Kind == AuthoringHistoryDocumentKind.PublicationSection
            ? DesignedPageContainerKind.PublicationSection
            : DesignedPageContainerKind.Chapter;
        var existing = await db.DesignedPagePlacementReferences
            .Where(item => item.ProjectId == target.ProjectId
                && item.ContainerKind == containerKind
                && item.ContainerId == target.DocumentId
                && item.EditionId == target.EditionId)
            .ToListAsync(cancellationToken);
        var blocks = document?.Content.Where(item => item.Type == ManuscriptBlockType.DesignedPage).ToList() ?? [];
        if (blocks.GroupBy(item => item.Id, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new InvalidDataException("A restored manuscript contains duplicate Designed Page placement IDs.");
        var desiredIds = blocks.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        db.DesignedPagePlacementReferences.RemoveRange(existing.Where(item => !desiredIds.Contains(item.Id)));
        var now = DateTime.UtcNow;
        foreach (var block in blocks)
        {
            var reference = existing.SingleOrDefault(item => item.Id == block.Id);
            if (reference is null)
            {
                reference = new DesignedPagePlacementReference
                {
                    ProjectId = target.ProjectId,
                    Id = block.Id,
                    ContainerKind = containerKind,
                    ContainerId = target.DocumentId,
                    EditionId = target.EditionId,
                    CreatedAt = now,
                };
                db.DesignedPagePlacementReferences.Add(reference);
            }
            reference.DesignedPageId = block.DesignedPageId!.Value;
            reference.ManuscriptRevision = document!.Revision;
            reference.UpdatedAt = now;
        }
    }

    private sealed record PreparedRestore(
        AuthoringHistoryTarget Target,
        bool Inherited,
        ManuscriptDocument Document);
}
