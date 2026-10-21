using Lorekeeper.Models;

namespace Lorekeeper.Llm;

public interface IOpenAiAccountService
{
    Task<IReadOnlyList<OpenAiAccount>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<OpenAiAccount?> GetByIdAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>Returns the account of this kind, creating it first; Lorekeeper keeps one account per kind.</summary>
    Task<OpenAiAccount> CreateAsync(
        OpenAiAccountKind kind,
        string? displayName = null,
        CancellationToken cancellationToken = default);

    /// <summary>Records that ChatGPT plan inference reported this plan or user as not eligible.</summary>
    Task MarkPlanIneligibleAsync(int accountId, CancellationToken cancellationToken = default);

    /// <summary>Records that the first-sign-in ChatGPT plan notice was dismissed.</summary>
    Task AcknowledgeWelcomeAsync(int accountId, CancellationToken cancellationToken = default);
}
