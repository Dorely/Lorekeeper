using Lorekeeper.Models;

namespace Lorekeeper.Persistence.Repositories;

public interface IOpenAiAccountRepository
{
    Task<List<OpenAiAccount>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<OpenAiAccount?> GetByIdAsync(int id, CancellationToken cancellationToken = default);
    Task AddAsync(OpenAiAccount account, CancellationToken cancellationToken = default);
    void Update(OpenAiAccount account);
}
