using Lorekeeper.Models;

namespace Lorekeeper.Projects;

public interface IBookBriefService
{
    Task<BookBrief> GetOrCreateAsync(Guid projectId, CancellationToken cancellationToken = default);

    Task<BookBrief> UpdateAsync(
        Guid projectId,
        BookBriefPatch patch,
        CancellationToken cancellationToken = default);

    Task<BookBrief> UpdateVisualDirectionAsync(
        Guid projectId,
        string expectedCurrentVisualDirection,
        string visualDirection,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BookBriefCanonSourceOption>> ListCanonSourceOptionsAsync(
        Guid projectId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BookBriefCanonSourceSummary>> ListCanonSourcesAsync(
        Guid projectId,
        CancellationToken cancellationToken = default);

    Task ReplaceCanonSourcesAsync(
        Guid projectId,
        IReadOnlyCollection<Guid> sourceIds,
        CancellationToken cancellationToken = default);

    string FormatForPrompt(BookBrief brief);
}

public sealed record BookBriefCanonSourceOption(
    Guid SourceId,
    string Title,
    string SourceKind,
    bool IsSelected);

public sealed record BookBriefCanonSourceSummary(
    Guid SourceId,
    string Title,
    string SourceKind);

public sealed class BookBriefVisualDirectionConflictException(string actualVisualDirection)
    : InvalidOperationException("The project Visual Direction changed before this update could be applied.")
{
    public string ActualVisualDirection { get; } = actualVisualDirection;
}
