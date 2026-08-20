using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Lorekeeper.Persistence.Repositories;

namespace Lorekeeper.Llm;

public class LlmProviderService(
IAppDatabaseOperationFactory database, ICodexAuthService codexAuth) : ILlmProviderService
{
    public async Task<List<LlmProvider>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var providers = databaseOperation.Repositories.LlmProviders;
        return await providers.GetAllAsync(cancellationToken);
    }
    public async Task<LlmProvider?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var providers = databaseOperation.Repositories.LlmProviders;
        return await providers.GetByIdAsync(id, cancellationToken);
    }
    public async Task<LlmProvider?> GetByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var providers = databaseOperation.Repositories.LlmProviders;
        return await providers.GetByNameAsync(name, cancellationToken);
    }
    public async Task<LlmProvider?> GetDefaultAsync(CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var providers = databaseOperation.Repositories.LlmProviders;
        return await providers.GetDefaultAsync(cancellationToken);
    }
    public async Task<ChatProviderAvailability> GetDefaultChatProviderAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        var all = await GetAllAsync(cancellationToken);
        return await ResolveDefaultChatProviderAvailabilityAsync(all, cancellationToken);
    }

    public async Task<VisionProviderAvailability> GetDefaultVisionProviderAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        var all = await GetAllAsync(cancellationToken);
        if (all.Count == 0)
            return VisionProviderAvailability.Unavailable("Configure and test a vision-capable provider in Settings > Providers to enable PDF image reading.");

        var explicitDefault = all.FirstOrDefault(provider => provider.IsDefault);
        if (explicitDefault is not null && await IsVisionProviderWorkingAsync(explicitDefault, cancellationToken))
            return VisionProviderAvailability.Available(explicitDefault);

        foreach (var provider in all.Where(provider => !provider.IsDefault))
        {
            if (await IsVisionProviderWorkingAsync(provider, cancellationToken))
                return VisionProviderAvailability.Available(provider);
        }

        var candidate = explicitDefault ?? all.FirstOrDefault();
        var reason = candidate is null
            ? "Configure and test a vision-capable provider in Settings > Providers to enable PDF image reading."
            : await GetVisionUnavailableReasonAsync(candidate, cancellationToken);
        return VisionProviderAvailability.Unavailable(reason, candidate);
    }

    public async Task<List<LlmProvider>> ListWorkingChatProvidersAsync(CancellationToken cancellationToken = default)
    {
        var all = await GetAllAsync(cancellationToken);
        return await ListWorkingChatProvidersAsync(all, cancellationToken);
    }

    public async Task<IReadOnlyList<ChatModelOption>> ListChatModelOptionsAsync(CancellationToken cancellationToken = default)
    {
        var all = await GetAllAsync(cancellationToken);
        var working = await ListWorkingChatProvidersAsync(all, cancellationToken);
        var defaultAvailability = await ResolveDefaultChatProviderAvailabilityAsync(all, cancellationToken);
        var defaultProviderId = defaultAvailability.IsAvailable ? defaultAvailability.Provider?.Id : null;

        return working
            .Select(provider =>
            {
                var labels = GetChatModelLabels(provider, all);
                return new ChatModelOption(
                    provider.Id,
                    labels.ConnectionLabel,
                    labels.ModelLabel,
                    labels.Label,
                    provider.Id == defaultProviderId,
                    provider);
            })
            .OrderByDescending(option => option.IsDefault)
            .ThenBy(option => option.ConnectionLabel, StringComparer.OrdinalIgnoreCase)
            .ThenBy(option => option.ModelLabel, StringComparer.OrdinalIgnoreCase)
            .ThenBy(option => option.ProviderId)
            .ToList();
    }

    public async Task<ChatModelSelection> ResolveChatModelSelectionAsync(
        int? selectedProviderId,
        CancellationToken cancellationToken = default)
    {
        var all = await GetAllAsync(cancellationToken);
        var defaultAvailability = await ResolveDefaultChatProviderAvailabilityAsync(all, cancellationToken);

        if (selectedProviderId is null)
        {
            var provider = defaultAvailability.Provider;
            if (provider is null)
            {
                return new ChatModelSelection(
                    null,
                    null,
                    null,
                    null,
                    null,
                    false,
                    false,
                    false,
                    false,
                    defaultAvailability.Message);
            }

            var labels = GetChatModelLabels(provider, all);
            return new ChatModelSelection(
                null,
                provider,
                labels.ConnectionLabel,
                labels.ModelLabel,
                labels.Label,
                defaultAvailability.IsAvailable,
                defaultAvailability.IsAvailable,
                false,
                false,
                defaultAvailability.Message);
        }

        var selected = all.FirstOrDefault(provider => provider.Id == selectedProviderId.Value);
        if (selected is null)
        {
            return new ChatModelSelection(
                selectedProviderId,
                null,
                null,
                null,
                $"Saved model {selectedProviderId.Value} (unavailable)",
                false,
                false,
                true,
                true,
                "This chat's selected model is no longer saved. Choose another available model or press Reset.");
        }

        var selectedLabels = GetChatModelLabels(selected, all);
        if (await IsChatProviderWorkingAsync(selected, cancellationToken))
        {
            var isDefault = defaultAvailability.IsAvailable
                && defaultAvailability.Provider?.Id == selected.Id;
            return new ChatModelSelection(
                selectedProviderId,
                selected,
                selectedLabels.ConnectionLabel,
                selectedLabels.ModelLabel,
                selectedLabels.Label,
                true,
                isDefault,
                true,
                false,
                string.Empty);
        }

        return new ChatModelSelection(
            selectedProviderId,
            selected,
            selectedLabels.ConnectionLabel,
            selectedLabels.ModelLabel,
            $"{selectedLabels.Label} (unavailable)",
            false,
            false,
            true,
            true,
            $"The selected model is unavailable. {await GetUnavailableReasonAsync(selected, cancellationToken)} Choose another available model or press Reset.");
    }

    public async Task<int?> NormalizeChatModelSelectionAsync(
        int? selectedProviderId,
        CancellationToken cancellationToken = default)
    {
        if (selectedProviderId is null)
            return null;

        var all = await GetAllAsync(cancellationToken);
        var defaultAvailability = await ResolveDefaultChatProviderAvailabilityAsync(all, cancellationToken);
        return defaultAvailability.IsAvailable
            && defaultAvailability.Provider?.Id == selectedProviderId
            ? null
            : selectedProviderId;
    }

    private async Task<List<LlmProvider>> ListWorkingChatProvidersAsync(
        IReadOnlyList<LlmProvider> all,
        CancellationToken cancellationToken)
    {
        var working = new List<LlmProvider>();
        foreach (var provider in all)
        {
            if (await IsChatProviderWorkingAsync(provider, cancellationToken))
                working.Add(provider);
        }

        return working;
    }

    private async Task<ChatProviderAvailability> ResolveDefaultChatProviderAvailabilityAsync(
        IReadOnlyList<LlmProvider> all,
        CancellationToken cancellationToken)
    {
        if (all.Count == 0)
            return ChatProviderAvailability.Unavailable("Configure and test a chat provider in Settings > Providers to enable LLM features.");

        var explicitDefault = all.FirstOrDefault(provider => provider.IsDefault);
        if (explicitDefault is not null && await IsChatProviderWorkingAsync(explicitDefault, cancellationToken))
            return ChatProviderAvailability.Available(explicitDefault);

        foreach (var provider in all.Where(provider => !provider.IsDefault))
        {
            if (await IsChatProviderWorkingAsync(provider, cancellationToken))
                return ChatProviderAvailability.Available(provider);
        }

        var candidate = explicitDefault ?? all.FirstOrDefault();
        var reason = candidate is null
            ? "Configure and test a chat provider in Settings > Providers to enable LLM features."
            : await GetUnavailableReasonAsync(candidate, cancellationToken);
        return ChatProviderAvailability.Unavailable(reason, candidate);
    }

    private static ChatModelLabels GetChatModelLabels(
        LlmProvider provider,
        IReadOnlyList<LlmProvider> all)
    {
        var connection = provider.CredentialSourceId is int sourceId
            ? all.FirstOrDefault(candidate => candidate.Id == sourceId) ?? provider
            : provider;
        var connectionLabel = BuildConnectionLabel(connection);
        var modelLabel = BuildModelLabel(provider);
        return new ChatModelLabels(connectionLabel, modelLabel, $"{connectionLabel} · {modelLabel}");
    }

    private static string BuildConnectionLabel(LlmProvider connection)
    {
        var displayName = FirstNonEmpty(connection.DisplayName, connection.Name, "Connection");
        var name = connection.Name?.Trim();
        return !string.IsNullOrWhiteSpace(name)
            && !string.Equals(displayName, name, StringComparison.OrdinalIgnoreCase)
            ? $"{displayName} ({name})"
            : displayName;
    }

    private static string BuildModelLabel(LlmProvider provider)
    {
        // A root row owns both the connection and its first model, so its
        // display name is the connection name rather than a distinct model
        // label. Use the actual model ID for that row to avoid repeating the
        // optgroup label; child rows retain their model display names.
        var displayName = provider.CredentialSourceId is null
            ? FirstNonEmpty(null, provider.ModelId, "Model")
            : FirstNonEmpty(provider.DisplayName, provider.ModelId, "Model");
        var modelId = provider.ModelId?.Trim();
        return !string.IsNullOrWhiteSpace(modelId)
            && !string.Equals(displayName, modelId, StringComparison.OrdinalIgnoreCase)
            ? $"{displayName} ({modelId})"
            : displayName;
    }

    private static string FirstNonEmpty(string? value, string? fallback, string finalFallback) =>
        !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : !string.IsNullOrWhiteSpace(fallback)
                ? fallback.Trim()
                : finalFallback;

    private sealed record ChatModelLabels(string ConnectionLabel, string ModelLabel, string Label);

    public async Task<List<LlmProvider>> ListWorkingVisionProvidersAsync(CancellationToken cancellationToken = default)
    {
        var all = await GetAllAsync(cancellationToken);
        var working = new List<LlmProvider>();
        foreach (var provider in all)
        {
            if (await IsVisionProviderWorkingAsync(provider, cancellationToken))
                working.Add(provider);
        }

        return working;
    }

    public async Task<bool> IsChatProviderWorkingAsync(int providerId, CancellationToken cancellationToken = default)
    {
        var provider = await GetByIdAsync(providerId, cancellationToken);
        return provider is not null && await IsChatProviderWorkingAsync(provider, cancellationToken);
    }

    public async Task<bool> IsVisionProviderWorkingAsync(int providerId, CancellationToken cancellationToken = default)
    {
        var provider = await GetByIdAsync(providerId, cancellationToken);
        return provider is not null && await IsVisionProviderWorkingAsync(provider, cancellationToken);
    }

    public async Task<bool> IsCodexConnectedAsync(CancellationToken cancellationToken = default)
    {
        var provider = await GetByNameAsync(CodexProvider.Name, cancellationToken);
        if (provider is null || !CodexProvider.IsCodex(provider))
            return false;

        try
        {
            return !string.IsNullOrWhiteSpace(await codexAuth.GetValidTokenAsync(provider.Id, cancellationToken));
        }
        catch
        {
            return false;
        }
    }

    public async Task<LlmProvider> CreateAsync(LlmProvider provider, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var providers = databaseOperation.Repositories.LlmProviders;
        provider.CreatedAt = DateTime.UtcNow;
        provider.UpdatedAt = DateTime.UtcNow;
        await providers.AddAsync(provider, cancellationToken);
        await databaseOperation.SaveChangesAsync(cancellationToken);
        return provider;
    }

    public async Task<LlmProvider> UpdateAsync(LlmProvider provider, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var providers = databaseOperation.Repositories.LlmProviders;
        provider.UpdatedAt = DateTime.UtcNow;
        providers.Update(provider);
        await databaseOperation.SaveChangesAsync(cancellationToken);
        return provider;
    }

    public async Task UpdateConnectionAsync(LlmConnectionUpdate update, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var providers = databaseOperation.Repositories.LlmProviders;
        var all = await providers.GetAllAsync(cancellationToken);
        var connection = all.FirstOrDefault(provider => provider.Id == update.Id)
            ?? throw new InvalidOperationException("Provider connection was not found.");
        if (connection.CredentialSourceId is not null)
            throw new InvalidOperationException("Only a top-level provider connection can update shared connection settings.");

        var affected = all.Where(provider => provider.Id == connection.Id || provider.CredentialSourceId == connection.Id).ToList();
        foreach (var provider in affected)
        {
            provider.EndpointUrl = update.EndpointUrl.Trim();
            provider.AuthType = update.AuthType;
            provider.ClearChatReadiness("Connection settings changed. Run Test successfully before using this model for chat.");
            provider.ClearVisionReadiness("Connection settings changed. Run Test successfully before using this model for image reading.");
            provider.UpdatedAt = DateTime.UtcNow;
            if (provider.Id == connection.Id)
            {
                provider.DisplayName = string.IsNullOrWhiteSpace(update.DisplayName) ? null : update.DisplayName.Trim();
                provider.ApiKey = update.AuthType == AuthType.ApiKey ? update.ApiKey?.Trim() : null;
            }
            else
            {
                provider.ApiKey = null;
            }
            providers.Update(provider);
        }

        await databaseOperation.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var providers = databaseOperation.Repositories.LlmProviders;
        var provider = await providers.GetByIdAsync(id, cancellationToken);
        if (provider is null) return;
        providers.Remove(provider);
        await databaseOperation.SaveChangesAsync(cancellationToken);
    }

    public async Task SetDefaultAsync(int id, CancellationToken cancellationToken = default)
    {
        if (!await IsChatProviderWorkingAsync(id, cancellationToken))
            throw new InvalidOperationException("Run Test successfully before setting this provider as the default chat provider.");

        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        var providers = databaseOperation.Repositories.LlmProviders;
        await providers.SetDefaultAsync(id, cancellationToken);
        await databaseOperation.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteConnectionAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var providers = databaseOperation.Repositories.LlmProviders;
        var all = await providers.GetAllAsync(cancellationToken);
        var connection = all.FirstOrDefault(provider => provider.Id == id);
        if (connection is null)
            return;
        if (connection.CredentialSourceId is not null)
            throw new InvalidOperationException("Nested models must be deleted individually.");

        foreach (var child in all.Where(provider => provider.CredentialSourceId == id))
            providers.Remove(child);
        providers.Remove(connection);
        await databaseOperation.SaveChangesAsync(cancellationToken);
    }

    public async Task<LlmProvider> MarkChatTestSucceededAsync(LlmProvider provider, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var providers = databaseOperation.Repositories.LlmProviders;
        var target = await ResolvePersistedProviderForTestAsync(provider, cancellationToken);
        target.MarkChatTestSucceeded(DateTime.UtcNow);
        if (target.Id == 0)
            return target;

        target.UpdatedAt = DateTime.UtcNow;
        providers.Update(target);
        await databaseOperation.SaveChangesAsync(cancellationToken);
        return target;
    }

    public async Task<LlmProvider> MarkChatTestFailedAsync(LlmProvider provider, string error, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var providers = databaseOperation.Repositories.LlmProviders;
        var target = await ResolvePersistedProviderForTestAsync(provider, cancellationToken);
        target.MarkChatTestFailed(error, DateTime.UtcNow);
        if (target.Id == 0)
            return target;

        target.UpdatedAt = DateTime.UtcNow;
        providers.Update(target);
        await databaseOperation.SaveChangesAsync(cancellationToken);
        return target;
    }

    public async Task<LlmProvider> MarkVisionTestSucceededAsync(LlmProvider provider, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var providers = databaseOperation.Repositories.LlmProviders;
        var target = await ResolvePersistedProviderForTestAsync(provider, cancellationToken);
        target.MarkVisionTestSucceeded(DateTime.UtcNow);
        if (target.Id == 0)
            return target;

        target.UpdatedAt = DateTime.UtcNow;
        providers.Update(target);
        await databaseOperation.SaveChangesAsync(cancellationToken);
        return target;
    }

    public async Task<LlmProvider> MarkVisionTestFailedAsync(LlmProvider provider, string error, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var providers = databaseOperation.Repositories.LlmProviders;
        var target = await ResolvePersistedProviderForTestAsync(provider, cancellationToken);
        target.MarkVisionTestFailed(error, DateTime.UtcNow);
        if (target.Id == 0)
            return target;

        target.UpdatedAt = DateTime.UtcNow;
        providers.Update(target);
        await databaseOperation.SaveChangesAsync(cancellationToken);
        return target;
    }

    public async Task<string?> GetEffectiveApiKeyAsync(int providerId, CancellationToken cancellationToken = default)
    {
        LlmProvider? credentialProvider;
        await using (var readOperation = await database.OpenReadAsync(cancellationToken))
        {
            var providers = readOperation.Repositories.LlmProviders;
            var provider = await providers.GetByIdAsync(providerId, cancellationToken);
            if (provider is null) return null;
            var credentialProviderId = provider.EffectiveCredentialProviderId;
            credentialProvider = credentialProviderId == providerId
                ? provider
                : await providers.GetByIdAsync(credentialProviderId, cancellationToken);
        }

        if (credentialProvider is null) return null;

        if (credentialProvider.AuthType == AuthType.ApiKey)
            return credentialProvider.ApiKey;

        if (credentialProvider.AuthType == AuthType.OAuth)
            return await codexAuth.GetValidTokenAsync(credentialProvider.Id, cancellationToken);

        return null;
    }

    private async Task<bool> IsChatProviderWorkingAsync(LlmProvider provider, CancellationToken cancellationToken)
    {
        if (!provider.HasCurrentChatTestSnapshot)
            return false;

        var credentials = await GetCredentialStatusAsync(provider, cancellationToken);
        return credentials.Available;
    }

    private async Task<bool> IsVisionProviderWorkingAsync(LlmProvider provider, CancellationToken cancellationToken)
    {
        if (!provider.HasCurrentVisionTestSnapshot)
            return false;

        var credentials = await GetCredentialStatusAsync(provider, cancellationToken);
        return credentials.Available;
    }

    private async Task<string> GetUnavailableReasonAsync(LlmProvider provider, CancellationToken cancellationToken)
    {
        if (!provider.LastChatTestSucceeded)
        {
            return string.IsNullOrWhiteSpace(provider.LastChatTestError)
                ? "Run Test successfully in Settings > Providers to enable LLM features."
                : $"The last provider test failed: {provider.LastChatTestError}";
        }

        if (!provider.HasCurrentChatTestSnapshot)
            return "Provider settings changed. Run Test successfully in Settings > Providers to enable LLM features.";

        var credentials = await GetCredentialStatusAsync(provider, cancellationToken);
        return credentials.Available
            ? string.Empty
            : credentials.Message;
    }

    private async Task<string> GetVisionUnavailableReasonAsync(LlmProvider provider, CancellationToken cancellationToken)
    {
        if (!provider.LastVisionTestSucceeded)
        {
            return string.IsNullOrWhiteSpace(provider.LastVisionTestError)
                ? "Run Test successfully in Settings > Providers to enable PDF image reading."
                : $"The last provider vision test failed: {provider.LastVisionTestError}";
        }

        if (!provider.HasCurrentVisionTestSnapshot)
            return "Provider settings changed. Run Test successfully in Settings > Providers to enable PDF image reading.";

        var credentials = await GetCredentialStatusAsync(provider, cancellationToken);
        return credentials.Available
            ? string.Empty
            : credentials.Message;
    }

    private async Task<CredentialStatus> GetCredentialStatusAsync(LlmProvider provider, CancellationToken cancellationToken)
    {
        LlmProvider? credentialProvider;
        await using (var readOperation = await database.OpenReadAsync(cancellationToken))
        {
            var credentialProviderId = provider.EffectiveCredentialProviderId;
            credentialProvider = credentialProviderId == provider.Id
                ? provider
                : await readOperation.Repositories.LlmProviders.GetByIdAsync(credentialProviderId, cancellationToken);
        }

        if (credentialProvider is null)
            return new CredentialStatus(false, "The credential source for this provider no longer exists.");

        if (credentialProvider.AuthType == AuthType.None)
            return new CredentialStatus(true, string.Empty);

        if (credentialProvider.AuthType == AuthType.ApiKey)
        {
            return !string.IsNullOrWhiteSpace(credentialProvider.ApiKey)
                ? new CredentialStatus(true, string.Empty)
                : new CredentialStatus(false, "Add an API key and run Test successfully in Settings > Providers.");
        }

        if (credentialProvider.AuthType == AuthType.OAuth)
        {
            try
            {
                return !string.IsNullOrWhiteSpace(await codexAuth.GetValidTokenAsync(credentialProvider.Id, cancellationToken))
                    ? new CredentialStatus(true, string.Empty)
                    : new CredentialStatus(false, "Connect OpenAI Codex and run Test successfully in Settings > Providers.");
            }
            catch (Exception ex)
            {
                return new CredentialStatus(false, $"Reconnect OpenAI Codex and run Test successfully in Settings > Providers. {ex.Message}");
            }
        }

        return new CredentialStatus(false, "Provider credentials are not configured.");
    }

    private async Task<LlmProvider> ResolvePersistedProviderForTestAsync(LlmProvider provider, CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var providers = databaseOperation.Repositories.LlmProviders;
        if (provider.Id == 0)
            return provider;

        var target = await providers.GetByIdAsync(provider.Id, cancellationToken)
            ?? throw new InvalidOperationException($"Provider {provider.Id} not found.");

        CopyEditableValues(provider, target);
        return target;
    }

    private static void CopyEditableValues(LlmProvider source, LlmProvider target)
    {
        target.Name = source.Name;
        target.DisplayName = source.DisplayName;
        target.EndpointUrl = source.EndpointUrl;
        target.ModelId = source.ModelId;
        target.ReasoningEffort = source.ReasoningEffort;
        target.AuthType = source.AuthType;
        target.ApiKey = source.ApiKey;
        target.IsDefault = source.IsDefault;
        target.CredentialSourceId = source.CredentialSourceId;
    }

    private sealed record CredentialStatus(bool Available, string Message);
}
