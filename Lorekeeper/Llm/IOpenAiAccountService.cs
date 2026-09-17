using Lorekeeper.Models;

namespace Lorekeeper.Llm;

public interface IOpenAiAccountService
{
    Task<IReadOnlyList<OpenAiAccount>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<OpenAiAccount?> GetByIdAsync(int id, CancellationToken cancellationToken = default);
    Task<OpenAiAccount> CreateAsync(string? displayName = null, CancellationToken cancellationToken = default);
}
