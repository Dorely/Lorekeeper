using Lorekeeper.Llm;

namespace Lorekeeper.Outline;

public interface IOutlineWorkingContextBuilder
{
    Task<IReadOnlyList<SystemPromptSourceSection>> BuildAsync(
        Guid projectId,
        CancellationToken cancellationToken = default);
}
