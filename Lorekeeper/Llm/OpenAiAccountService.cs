using Lorekeeper.Models;
using Lorekeeper.Persistence;

namespace Lorekeeper.Llm;

public sealed class OpenAiAccountService(IAppDatabaseOperationFactory database) : IOpenAiAccountService
{
    public async Task<IReadOnlyList<OpenAiAccount>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var operation = await database.OpenReadAsync(cancellationToken);
        return await operation.Repositories.OpenAiAccounts.GetAllAsync(cancellationToken);
    }

    public async Task<OpenAiAccount?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var operation = await database.OpenReadAsync(cancellationToken);
        return await operation.Repositories.OpenAiAccounts.GetByIdAsync(id, cancellationToken);
    }

    public async Task<OpenAiAccount> CreateAsync(
        string? displayName = null,
        CancellationToken cancellationToken = default)
    {
        await using var operation = await database.OpenWriteAsync(cancellationToken);
        var account = new OpenAiAccount
        {
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? "OpenAI account" : displayName.Trim(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        await operation.Repositories.OpenAiAccounts.AddAsync(account, cancellationToken);
        await operation.SaveChangesAsync(cancellationToken);
        return account;
    }
}
