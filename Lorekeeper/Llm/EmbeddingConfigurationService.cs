using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Lorekeeper.Persistence.Repositories;

namespace Lorekeeper.Llm;

public sealed class EmbeddingConfigurationService(
IAppDatabaseOperationFactory database, IEmbeddingClient client, IEmbeddingRebuildQueue rebuildQueue) : IEmbeddingConfigurationService
{
    public async Task<EmbeddingConfiguration?> GetActiveAsync(CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var configurations = databaseOperation.Repositories.EmbeddingConfigurations;
        return await configurations.GetAsync(cancellationToken);
    }
    public async Task<IReadOnlyList<LlmProvider>> ListConnectionProvidersAsync(CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var providers = databaseOperation.Repositories.LlmProviders;
        return (await providers.GetAllAsync(cancellationToken))
                    .Where(provider => provider.CredentialSourceId is null)
                    .OrderBy(provider => provider.DisplayName ?? provider.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
    }
    public async Task<EmbeddingTestResult> TestAsync(EmbeddingTestRequest request, CancellationToken cancellationToken = default)
    {
        LlmProvider provider;
        await using (var readOperation = await database.OpenReadAsync(cancellationToken))
        {
            provider = await readOperation.Repositories.LlmProviders.GetByIdAsync(request.ProviderId, cancellationToken)
                ?? throw new InvalidOperationException($"Provider connection {request.ProviderId} was not found.");
        }
        if (provider.CredentialSourceId is not null)
            throw new InvalidOperationException("Embeddings must use a top-level provider connection, not a child model row.");
        ValidateProviderApiKind(provider, request.ApiKind);

        var modelId = NormalizeModelId(request.ModelId);
        var embeddings = await client.GenerateAsync(provider, request.ApiKind, modelId, ["Lorekeeper embedding test"], cancellationToken);
        var vector = embeddings.Count == 0 ? throw new InvalidOperationException("Embedding endpoint returned no vectors.") : embeddings[0];
        if (vector.Length <= 0)
            throw new InvalidOperationException("Embedding endpoint returned an empty vector.");

        return new EmbeddingTestResult(provider.Id, request.ApiKind, modelId, vector.Length, DateTime.UtcNow);
    }

    public async Task<EmbeddingSaveResult> SaveAsync(EmbeddingConfigurationDraft draft, CancellationToken cancellationToken = default)
    {
        ValidateTestedDraft(draft);

        bool changed;
        await using (var readOperation = await database.OpenReadAsync(cancellationToken))
        {
            var provider = await readOperation.Repositories.LlmProviders.GetByIdAsync(draft.ProviderId, cancellationToken)
                ?? throw new InvalidOperationException($"Provider connection {draft.ProviderId} was not found.");
            if (provider.CredentialSourceId is not null)
                throw new InvalidOperationException("Embeddings must use a top-level provider connection, not a child model row.");
            ValidateProviderApiKind(provider, draft.ApiKind);

            var snapshot = await readOperation.Repositories.EmbeddingConfigurations.GetAsync(cancellationToken);
            changed = HasChanged(snapshot, draft);
        }

        if (changed)
            await rebuildQueue.CancelActiveAndClearPendingAsync(cancellationToken);

        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        var configurations = databaseOperation.Repositories.EmbeddingConfigurations;
        var existing = await configurations.GetAsync(cancellationToken);
        changed = HasChanged(existing, draft);

        var now = DateTime.UtcNow;
        var configuration = existing ?? new EmbeddingConfiguration
        {
            Id = EmbeddingConfiguration.SingletonId,
            ProviderId = draft.ProviderId,
            ApiKind = draft.ApiKind,
            ModelId = draft.ModelId.Trim(),
            Dimensions = draft.TestedDimensions,
            LastTestedProviderId = draft.TestedProviderId,
            LastTestedApiKind = draft.TestedApiKind,
            LastTestedModelId = draft.TestedModelId.Trim(),
            LastTestedDimensions = draft.TestedDimensions,
            LastTestedAt = now,
        };

        configuration.ProviderId = draft.ProviderId;
        configuration.ApiKind = draft.ApiKind;
        configuration.ModelId = draft.ModelId.Trim();
        configuration.Dimensions = draft.TestedDimensions;
        configuration.LastTestedProviderId = draft.TestedProviderId;
        configuration.LastTestedApiKind = draft.TestedApiKind;
        configuration.LastTestedModelId = draft.TestedModelId.Trim();
        configuration.LastTestedDimensions = draft.TestedDimensions;
        configuration.LastTestedAt = now;
        configuration.UpdatedAt = now;

        if (existing is null)
            await configurations.AddAsync(configuration, cancellationToken);
        else
            configurations.Update(configuration);

        await databaseOperation.SaveChangesAsync(cancellationToken);

        if (changed)
            rebuildQueue.Enqueue(new EmbeddingRebuildRequest(configuration.Id, now));

        return new EmbeddingSaveResult(configuration, changed);
    }

    public async Task<bool> ConfigureCodexDefaultIfUnsetAsync(int providerId, CancellationToken cancellationToken = default)
    {
        LlmProvider provider;
        await using (var readOperation = await database.OpenReadAsync(cancellationToken))
        {
            if (await readOperation.Repositories.EmbeddingConfigurations.GetAsync(cancellationToken) is not null)
                return false;
            provider = await readOperation.Repositories.LlmProviders.GetByIdAsync(providerId, cancellationToken)
                ?? throw new InvalidOperationException($"Provider connection {providerId} was not found.");
        }
        if (provider.CredentialSourceId is not null)
            throw new InvalidOperationException("Embeddings must use a top-level provider connection, not a child model row.");
        if (!CodexProvider.IsCodex(provider))
            throw new InvalidOperationException("Automatic Codex embeddings can only be configured for the OpenAI Codex provider.");

        var test = await TestAsync(
            new EmbeddingTestRequest(provider.Id, EmbeddingApiKind.OpenAICompatible, CodexProvider.DefaultEmbeddingModel),
            cancellationToken);

        await using (var readOperation = await database.OpenReadAsync(cancellationToken))
        {
            if (await readOperation.Repositories.EmbeddingConfigurations.GetAsync(cancellationToken) is not null)
                return false;
        }

        await SaveAsync(
            new EmbeddingConfigurationDraft(
                provider.Id,
                EmbeddingApiKind.OpenAICompatible,
                test.ModelId,
                test.Dimensions,
                test.ProviderId,
                test.ApiKind,
                test.ModelId),
            cancellationToken);

        return true;
    }

    public async Task<EmbeddingUnsetResult> UnsetAsync(CancellationToken cancellationToken = default)
    {
        await using (var readOperation = await database.OpenReadAsync(cancellationToken))
        {
            if (await readOperation.Repositories.EmbeddingConfigurations.GetAsync(cancellationToken) is null)
                return new EmbeddingUnsetResult(Removed: false);
        }

        await rebuildQueue.CancelActiveAndClearPendingAsync(cancellationToken);

        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        var configurations = databaseOperation.Repositories.EmbeddingConfigurations;
        var existing = await configurations.GetAsync(cancellationToken);
        if (existing is null)
            return new EmbeddingUnsetResult(Removed: false);
        configurations.Remove(existing);
        await databaseOperation.SaveChangesAsync(cancellationToken);

        return new EmbeddingUnsetResult(Removed: true);
    }

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var configurations = databaseOperation.Repositories.EmbeddingConfigurations;
        var active = await configurations.GetAsync(cancellationToken);
        return active is not null
            && active.Provider is not null
            && active.Dimensions > 0
            && !string.IsNullOrWhiteSpace(active.ModelId);
    }

    private static void ValidateTestedDraft(EmbeddingConfigurationDraft draft)
    {
        if (draft.ProviderId <= 0) throw new InvalidOperationException("Select a provider connection.");
        if (string.IsNullOrWhiteSpace(draft.ModelId)) throw new InvalidOperationException("Embedding model id is required.");
        if (draft.TestedDimensions <= 0) throw new InvalidOperationException("Run Test successfully before saving this embedding model.");
        if (draft.ProviderId != draft.TestedProviderId
            || draft.ApiKind != draft.TestedApiKind
            || !string.Equals(draft.ModelId.Trim(), draft.TestedModelId.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Run Test successfully before saving this embedding model.");
        }
    }

    private static string NormalizeModelId(string modelId) =>
        string.IsNullOrWhiteSpace(modelId)
            ? throw new InvalidOperationException("Embedding model id is required.")
            : modelId.Trim();

    private static bool HasChanged(EmbeddingConfiguration? existing, EmbeddingConfigurationDraft draft) =>
        existing is null
        || existing.ProviderId != draft.ProviderId
        || existing.ApiKind != draft.ApiKind
        || !string.Equals(existing.ModelId, draft.ModelId.Trim(), StringComparison.Ordinal)
        || existing.Dimensions != draft.TestedDimensions;

    private static void ValidateProviderApiKind(LlmProvider provider, EmbeddingApiKind apiKind)
    {
        if (CodexProvider.IsCodex(provider) && apiKind != EmbeddingApiKind.OpenAICompatible)
            throw new InvalidOperationException("OpenAI Codex embeddings only support the OpenAI-compatible embedding API.");
    }
}
