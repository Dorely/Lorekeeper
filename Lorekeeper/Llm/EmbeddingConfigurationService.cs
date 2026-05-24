using Lorekeeper.Models;
using Lorekeeper.Persistence.Repositories;

namespace Lorekeeper.Llm;

public sealed class EmbeddingConfigurationService(
    IEmbeddingConfigurationRepository configurations,
    ILlmProviderRepository providers,
    IEmbeddingClient client,
    IEmbeddingRebuildQueue rebuildQueue) : IEmbeddingConfigurationService
{
    public Task<EmbeddingConfiguration?> GetActiveAsync(CancellationToken cancellationToken = default) =>
        configurations.GetAsync(cancellationToken);

    public async Task<IReadOnlyList<LlmProvider>> ListConnectionProvidersAsync(CancellationToken cancellationToken = default) =>
        (await providers.GetAllAsync(cancellationToken))
            .Where(provider => provider.CredentialSourceId is null)
            .OrderBy(provider => provider.DisplayName ?? provider.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public async Task<EmbeddingTestResult> TestAsync(EmbeddingTestRequest request, CancellationToken cancellationToken = default)
    {
        var provider = await providers.GetByIdAsync(request.ProviderId, cancellationToken)
            ?? throw new InvalidOperationException($"Provider connection {request.ProviderId} was not found.");
        if (provider.CredentialSourceId is not null)
            throw new InvalidOperationException("Embeddings must use a top-level provider connection, not a child model row.");

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

        var provider = await providers.GetByIdAsync(draft.ProviderId, cancellationToken)
            ?? throw new InvalidOperationException($"Provider connection {draft.ProviderId} was not found.");
        if (provider.CredentialSourceId is not null)
            throw new InvalidOperationException("Embeddings must use a top-level provider connection, not a child model row.");

        var existing = await configurations.GetAsync(cancellationToken);
        var changed = existing is null
            || existing.ProviderId != draft.ProviderId
            || existing.ApiKind != draft.ApiKind
            || !string.Equals(existing.ModelId, draft.ModelId.Trim(), StringComparison.Ordinal)
            || existing.Dimensions != draft.TestedDimensions;

        if (changed)
            await rebuildQueue.CancelActiveAndClearPendingAsync(cancellationToken);

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

        await configurations.SaveChangesAsync(cancellationToken);

        if (changed)
            rebuildQueue.Enqueue(new EmbeddingRebuildRequest(configuration.Id, now));

        return new EmbeddingSaveResult(configuration, changed);
    }

    public async Task<EmbeddingUnsetResult> UnsetAsync(CancellationToken cancellationToken = default)
    {
        var existing = await configurations.GetAsync(cancellationToken);
        if (existing is null)
            return new EmbeddingUnsetResult(Removed: false);

        await rebuildQueue.CancelActiveAndClearPendingAsync(cancellationToken);

        configurations.Remove(existing);
        await configurations.SaveChangesAsync(cancellationToken);

        return new EmbeddingUnsetResult(Removed: true);
    }

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
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
}
