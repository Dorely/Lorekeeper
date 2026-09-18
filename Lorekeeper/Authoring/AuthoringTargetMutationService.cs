using Lorekeeper.Composition;
using Lorekeeper.Manuscripts;
using Lorekeeper.Publish;

namespace Lorekeeper.Authoring;

public interface IAuthoringTargetMutationService
{
    Task ApplyAsync(Guid projectId, string targetId, long expectedRevision, IReadOnlyList<ManuscriptOperation> operations, CancellationToken cancellationToken);
    Task ReplaceAsync(Guid projectId, string targetId, long expectedRevision, ManuscriptDocument document, CancellationToken cancellationToken);
    Task RefreshAsync(Guid projectId, string targetId, CancellationToken cancellationToken);
}

/// <summary>Routes already-coordinated manuscript mutations to their owning services.</summary>
internal sealed class AuthoringTargetMutationService(
    IManuscriptService manuscripts,
    IPublicationSectionService publicationSections,
    IDesignedPageService designedPages) : IAuthoringTargetMutationService
{
    public async Task ApplyAsync(
        Guid projectId,
        string targetId,
        long expectedRevision,
        IReadOnlyList<ManuscriptOperation> operations,
        CancellationToken cancellationToken)
    {
        var target = AuthoringPersistence.ParseTarget(projectId, targetId);
        switch (target.HistoryTarget.Kind)
        {
            case AuthoringHistoryDocumentKind.CoreChapter:
            case AuthoringHistoryDocumentKind.EditionChapter:
                _ = await manuscripts.ApplyPersistedUnderProjectMutationLeaseAsync(
                    target.ContentTarget,
                    target.HistoryTarget.DocumentId,
                    expectedRevision,
                    operations,
                    cancellationToken);
                break;
            case AuthoringHistoryDocumentKind.PublicationSection:
                _ = await publicationSections.ApplyAuthoringOperationsAsync(
                    new(target.HistoryTarget.ProjectId, target.HistoryTarget.EditionId),
                    target.HistoryTarget.DocumentId,
                    expectedRevision,
                    operations,
                    cancellationToken);
                break;
            case AuthoringHistoryDocumentKind.DesignedPageContent:
                _ = await designedPages.ApplyAuthoringOperationsAsync(
                    target.ContentTarget,
                    target.HistoryTarget.ProjectId,
                    target.HistoryTarget.DocumentId,
                    expectedRevision,
                    operations,
                    cancellationToken);
                break;
            default:
                throw new InvalidOperationException("The authoring target does not support batch manuscript operations.");
        }
    }

    public async Task ReplaceAsync(
        Guid projectId,
        string targetId,
        long expectedRevision,
        ManuscriptDocument document,
        CancellationToken cancellationToken)
    {
        var target = AuthoringPersistence.ParseTarget(projectId, targetId);
        if (target.HistoryTarget.Kind is AuthoringHistoryDocumentKind.CoreChapter or AuthoringHistoryDocumentKind.EditionChapter)
        {
            _ = await manuscripts.ReplaceDocumentAsync(
                target.ContentTarget,
                target.HistoryTarget.DocumentId,
                expectedRevision,
                document,
                cancellationToken);
            return;
        }
        if (target.HistoryTarget.Kind == AuthoringHistoryDocumentKind.PublicationSection)
        {
            var sectionTarget = new PublicationSectionTarget(target.HistoryTarget.ProjectId, target.HistoryTarget.EditionId);
            var current = await publicationSections.GetAsync(sectionTarget, target.HistoryTarget.DocumentId, cancellationToken);
            _ = await publicationSections.UpsertAsync(sectionTarget, new(
                current.Id,
                current.Title,
                current.Kind,
                current.Anchor,
                current.TargetKind,
                current.TargetId,
                current.InclusionMode,
                current.StartSide,
                ManuscriptCodec.Serialize(document with { ManuscriptId = current.Id, Revision = expectedRevision }),
                expectedRevision), cancellationToken);
            return;
        }
        if (target.HistoryTarget.Kind == AuthoringHistoryDocumentKind.DesignedPageContent)
        {
            _ = await designedPages.ReplaceAuthoringDocumentAsync(
                target.ContentTarget,
                target.HistoryTarget.ProjectId,
                target.HistoryTarget.DocumentId,
                expectedRevision,
                document,
                cancellationToken);
            return;
        }
        throw new InvalidOperationException("The authoring target does not support canonical restore.");
    }


    public async Task RefreshAsync(Guid projectId, string targetId, CancellationToken cancellationToken)
    {
        var parsed = AuthoringPersistence.ParseTarget(projectId, targetId);
        switch (parsed.HistoryTarget.Kind)
        {
            case AuthoringHistoryDocumentKind.CoreChapter:
            case AuthoringHistoryDocumentKind.EditionChapter:
                await manuscripts.RefreshDerivedStateAsync(
                    parsed.ContentTarget,
                    parsed.HistoryTarget.DocumentId,
                    cancellationToken);
                break;
            case AuthoringHistoryDocumentKind.PublicationSection:
                await publicationSections.RefreshAuthoringDerivedStateAsync(
                    new(projectId, parsed.HistoryTarget.EditionId),
                    parsed.HistoryTarget.DocumentId,
                    cancellationToken);
                break;
            case AuthoringHistoryDocumentKind.DesignedPageContent:
                await designedPages.RefreshAuthoringDerivedStateAsync(
                    parsed.ContentTarget,
                    projectId,
                    parsed.HistoryTarget.DocumentId,
                    cancellationToken);
                break;
        }

    }
}
