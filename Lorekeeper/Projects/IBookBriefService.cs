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

    string FormatForPrompt(BookBrief brief);
}

public sealed class BookBriefVisualDirectionConflictException(string actualVisualDirection)
    : InvalidOperationException("The project Visual Direction changed before this update could be applied.")
{
    public string ActualVisualDirection { get; } = actualVisualDirection;
}
