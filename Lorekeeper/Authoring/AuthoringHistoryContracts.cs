namespace Lorekeeper.Authoring;

public enum AuthoringHistoryDocumentKind
{
    CoreChapter,
    EditionChapter,
    PublicationSection,
    DesignedPageContent,
    CoreCover,
    ReleaseCover,
}

public enum AuthoringHistoryDependencyKind
{
    ProjectImage,
    ProjectFont,
    DesignedPage,
}

public sealed record AuthoringHistoryTarget(
    Guid ProjectId,
    AuthoringHistoryDocumentKind Kind,
    Guid DocumentId,
    Guid? EditionId = null);

public sealed record AuthoringHistoryState(
    bool CanUndo,
    bool CanRedo,
    string? UndoLabel,
    string? RedoLabel);
