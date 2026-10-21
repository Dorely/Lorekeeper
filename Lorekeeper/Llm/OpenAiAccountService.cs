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
        OpenAiAccountKind kind,
        string? displayName = null,
        CancellationToken cancellationToken = default)
    {
        await using var operation = await database.OpenWriteAsync(cancellationToken);
        var existing = (await operation.Repositories.OpenAiAccounts.GetAllAsync(cancellationToken))
            .FirstOrDefault(account => account.Kind == kind);
        if (existing is not null)
            return existing;

        var account = new OpenAiAccount
        {
            Kind = kind,
            DisplayName = string.IsNullOrWhiteSpace(displayName)
                ? kind == OpenAiAccountKind.ChatGptPlan ? "ChatGPT plan" : "OpenAI account"
                : displayName.Trim(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        await operation.Repositories.OpenAiAccounts.AddAsync(account, cancellationToken);
        await operation.SaveChangesAsync(cancellationToken);
        return account;
    }

    public async Task MarkPlanIneligibleAsync(int accountId, CancellationToken cancellationToken = default)
    {
        await using var operation = await database.OpenWriteAsync(cancellationToken);
        var account = await operation.Repositories.OpenAiAccounts.GetByIdAsync(accountId, cancellationToken);
        if (account is null || account.Kind != OpenAiAccountKind.ChatGptPlan)
            return;

        account.PlanIneligibleAt = DateTime.UtcNow;
        account.UpdatedAt = DateTime.UtcNow;
        operation.Repositories.OpenAiAccounts.Update(account);
        await operation.SaveChangesAsync(cancellationToken);
    }

    public async Task AcknowledgeWelcomeAsync(int accountId, CancellationToken cancellationToken = default)
    {
        await using var operation = await database.OpenWriteAsync(cancellationToken);
        var account = await operation.Repositories.OpenAiAccounts.GetByIdAsync(accountId, cancellationToken);
        if (account is null
            || account.Kind != OpenAiAccountKind.ChatGptPlan
            || account.WelcomeAcknowledgedAt is not null)
        {
            return;
        }

        account.WelcomeAcknowledgedAt = DateTime.UtcNow;
        account.UpdatedAt = DateTime.UtcNow;
        operation.Repositories.OpenAiAccounts.Update(account);
        await operation.SaveChangesAsync(cancellationToken);
    }
}
