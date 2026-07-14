using Lorekeeper.Models;

namespace Lorekeeper.Projects;

public interface IBookBriefService
{
    Task<BookBrief> GetOrCreateAsync(Guid projectId, CancellationToken cancellationToken = default);

    Task<BookBrief> UpdateAsync(
        Guid projectId,
        BookBriefPatch patch,
        CancellationToken cancellationToken = default);

    string FormatForPrompt(BookBrief brief);
}
