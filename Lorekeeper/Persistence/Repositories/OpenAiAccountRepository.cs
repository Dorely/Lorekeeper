using Lorekeeper.Models;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Persistence.Repositories;

public sealed class OpenAiAccountRepository(AppDatabaseReadOperation operation) : IOpenAiAccountRepository
{
    public Task<List<OpenAiAccount>> GetAllAsync(CancellationToken cancellationToken = default) =>
        operation.Db.OpenAiAccounts
            .AsNoTracking()
            .OrderBy(account => account.Id)
            .ToListAsync(cancellationToken);

    public Task<OpenAiAccount?> GetByIdAsync(int id, CancellationToken cancellationToken = default) =>
        operation.Db.OpenAiAccounts.FirstOrDefaultAsync(account => account.Id == id, cancellationToken);

    public Task AddAsync(OpenAiAccount account, CancellationToken cancellationToken = default) =>
        operation.Db.OpenAiAccounts.AddAsync(account, cancellationToken).AsTask();

    public void Update(OpenAiAccount account) => operation.Db.MarkModified(account);
}
