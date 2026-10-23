using System.Text.RegularExpressions;
using Lorekeeper.Composition;
using Lorekeeper.Models;
using Lorekeeper.Persistence;

namespace Lorekeeper.Images;

public sealed partial class ImageProviderService(
    IAppDatabaseOperationFactory database,
    IImageProviderClientFactory factory) : IImageProviderService
{
    public async Task<List<ImageProvider>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        return await databaseOperation.Repositories.ImageProviders.GetAllAsync(cancellationToken);
    }

    public async Task<ImageProvider?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        return await databaseOperation.Repositories.ImageProviders.GetByIdAsync(id, cancellationToken);
    }

    public async Task<ImageProvider> CreateAsync(ImageProvider provider, CancellationToken cancellationToken = default)
    {
        Normalize(provider);
        Validate(provider);
        var models = provider.Models.ToList();
        foreach (var model in models)
            NormalizeAndValidateModel(model);
        if (models.Select(model => model.ModelId).Distinct(StringComparer.Ordinal).Count() != models.Count)
            throw new ArgumentException("Each image model may appear only once per provider.", nameof(provider));

        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        var providers = databaseOperation.Repositories.ImageProviders;
        await ValidateAccountAsync(databaseOperation, provider, cancellationToken);
        if (await providers.GetByNameAsync(provider.Name, cancellationToken) is not null)
            throw new ArgumentException($"An image provider named '{provider.Name}' already exists.", nameof(provider));

        var makeDefault = provider.IsDefault || (await providers.GetAllAsync(cancellationToken)).Count == 0;
        var now = DateTime.UtcNow;
        provider.IsDefault = false;
        provider.CreatedAt = now;
        provider.UpdatedAt = now;
        provider.Models = [];
        await providers.AddAsync(provider, cancellationToken);
        await databaseOperation.SaveChangesAsync(cancellationToken);

        var defaultAssigned = false;
        for (var index = 0; index < models.Count; index++)
        {
            var model = models[index];
            model.Id = 0;
            model.ImageProviderId = provider.Id;
            model.ImageProvider = null;
            model.IsDefault = !defaultAssigned && (model.IsDefault || index == models.Count - 1 && !models.Any(candidate => candidate.IsDefault));
            defaultAssigned |= model.IsDefault;
            model.CreatedAt = now;
            model.UpdatedAt = now;
            await providers.AddModelAsync(model, cancellationToken);
        }
        if (makeDefault)
            await providers.SetDefaultAsync(provider.Id, cancellationToken);
        await databaseOperation.SaveChangesAsync(cancellationToken);
        if (models.Count > 0 && !models.Any(model => model.IsDefault))
            throw new InvalidOperationException("Image provider models were saved without a default.");
        return (await providers.GetByIdAsync(provider.Id, cancellationToken))!;
    }

    public async Task<ImageProvider> UpdateAsync(ImageProvider provider, CancellationToken cancellationToken = default)
    {
        Normalize(provider);
        Validate(provider);
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        var providers = databaseOperation.Repositories.ImageProviders;
        var stored = await providers.GetByIdAsync(provider.Id, cancellationToken)
            ?? throw new InvalidOperationException($"Image provider {provider.Id} was not found.");
        if (stored.Kind != provider.Kind)
            throw new ArgumentException("An image provider's kind cannot change. Add a new provider instead.", nameof(provider));
        if (await providers.GetByNameAsync(provider.Name, cancellationToken) is { } named && named.Id != provider.Id)
            throw new ArgumentException($"An image provider named '{provider.Name}' already exists.", nameof(provider));
        await ValidateAccountAsync(databaseOperation, provider, cancellationToken);

        stored.Name = provider.Name;
        stored.DisplayName = provider.DisplayName;
        stored.EndpointUrl = provider.EndpointUrl;
        stored.ApiKey = provider.ApiKey;
        stored.OpenAiAccountId = provider.OpenAiAccountId;
        stored.ConfigurationJson = provider.ConfigurationJson;
        stored.LastTestedAt = provider.LastTestedAt;
        stored.LastTestSucceeded = provider.LastTestSucceeded;
        stored.LastTestMessage = provider.LastTestMessage;
        stored.UpdatedAt = DateTime.UtcNow;
        if (provider.IsDefault && !stored.IsDefault)
            await providers.SetDefaultAsync(stored.Id, cancellationToken);
        await databaseOperation.SaveChangesAsync(cancellationToken);
        return (await providers.GetByIdAsync(stored.Id, cancellationToken))!;
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        var providers = databaseOperation.Repositories.ImageProviders;
        var provider = await providers.GetByIdAsync(id, cancellationToken);
        if (provider is null)
            return;

        // Jobs keep their soft ImageProviderId so history survives; queued work then fails clearly.
        providers.Remove(provider);
        await databaseOperation.SaveChangesAsync(cancellationToken);
        if (!provider.IsDefault)
            return;

        var next = (await providers.GetAllAsync(cancellationToken)).FirstOrDefault();
        if (next is not null)
        {
            await providers.SetDefaultAsync(next.Id, cancellationToken);
            await databaseOperation.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task SetDefaultAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        var providers = databaseOperation.Repositories.ImageProviders;
        var provider = await providers.GetByIdAsync(id, cancellationToken)
            ?? throw new InvalidOperationException($"Image provider {id} was not found.");
        if (provider.Models.Count == 0)
            throw new InvalidOperationException("Add or refresh at least one model before making this the default image provider.");

        await providers.SetDefaultAsync(id, cancellationToken);
        await databaseOperation.SaveChangesAsync(cancellationToken);
    }

    public async Task<ImageProviderModel> SaveModelAsync(int providerId, ImageProviderModel model, CancellationToken cancellationToken = default)
    {
        NormalizeAndValidateModel(model);
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        var providers = databaseOperation.Repositories.ImageProviders;
        var provider = await providers.GetByIdAsync(providerId, cancellationToken)
            ?? throw new InvalidOperationException($"Image provider {providerId} was not found.");
        if (provider.Models.Any(existing => existing.Id != model.Id && existing.ModelId == model.ModelId))
            throw new ArgumentException($"{provider.DisplayName ?? provider.Name} already has a model '{model.ModelId}'.", nameof(model));

        var now = DateTime.UtcNow;
        ImageProviderModel saved;
        if (model.Id == 0)
        {
            model.ImageProviderId = providerId;
            model.ImageProvider = null;
            model.IsDefault = provider.Models.Count == 0 || model.IsDefault;
            model.SortOrder = provider.Models.Count == 0 ? 0 : provider.Models.Max(existing => existing.SortOrder) + 1;
            model.CreatedAt = now;
            model.UpdatedAt = now;
            await providers.AddModelAsync(model, cancellationToken);
            saved = model;
        }
        else
        {
            saved = provider.Models.FirstOrDefault(existing => existing.Id == model.Id)
                ?? throw new InvalidOperationException($"Image model {model.Id} does not belong to this provider.");
            CopyModel(model, saved);
            saved.UpdatedAt = now;
        }

        await databaseOperation.SaveChangesAsync(cancellationToken);
        if (model.IsDefault)
        {
            await providers.SetDefaultModelAsync(providerId, saved.Id, cancellationToken);
            await databaseOperation.SaveChangesAsync(cancellationToken);
        }
        return saved;
    }

    public async Task DeleteModelAsync(int modelId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        var providers = databaseOperation.Repositories.ImageProviders;
        var model = await providers.GetModelAsync(modelId, cancellationToken);
        if (model is null)
            return;

        providers.RemoveModel(model);
        await databaseOperation.SaveChangesAsync(cancellationToken);
        if (!model.IsDefault)
            return;

        var provider = await providers.GetByIdAsync(model.ImageProviderId, cancellationToken);
        if (provider?.Models.FirstOrDefault() is { } next)
        {
            await providers.SetDefaultModelAsync(provider.Id, next.Id, cancellationToken);
            await databaseOperation.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task SetDefaultModelAsync(int modelId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        var providers = databaseOperation.Repositories.ImageProviders;
        var model = await providers.GetModelAsync(modelId, cancellationToken)
            ?? throw new InvalidOperationException($"Image model {modelId} was not found.");
        await providers.SetDefaultModelAsync(model.ImageProviderId, model.Id, cancellationToken);
        await databaseOperation.SaveChangesAsync(cancellationToken);
    }

    public async Task<ImageProviderTestResult> TestAsync(ImageProvider provider, CancellationToken cancellationToken = default)
    {
        ImageProviderTestResult result;
        try
        {
            Normalize(provider);
            Validate(provider);
            result = await factory.Get(provider.Kind).TestAsync(provider, cancellationToken);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or HttpRequestException or ProjectImageProviderException)
        {
            result = new ImageProviderTestResult(false, ex.Message);
        }

        if (provider.Id > 0)
            await RecordTestAsync(provider.Id, result, cancellationToken);
        return result;
    }

    private async Task RecordTestAsync(int providerId, ImageProviderTestResult result, CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        if (await databaseOperation.Repositories.ImageProviders.GetByIdAsync(providerId, cancellationToken) is not { } stored)
            return;
        stored.LastTestedAt = DateTime.UtcNow;
        stored.LastTestSucceeded = result.Success;
        stored.LastTestMessage = result.Message;
        await databaseOperation.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DiscoveredImageModel>> DiscoverModelsAsync(ImageProvider provider, CancellationToken cancellationToken = default)
    {
        Normalize(provider);
        Validate(provider);
        return await factory.Get(provider.Kind).DiscoverModelsAsync(provider, cancellationToken);
    }

    public async Task<int> RefreshModelsAsync(int providerId, CancellationToken cancellationToken = default)
    {
        var provider = await GetByIdAsync(providerId, cancellationToken)
            ?? throw new InvalidOperationException($"Image provider {providerId} was not found.");
        var discovered = await factory.Get(provider.Kind).DiscoverModelsAsync(provider, cancellationToken);

        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        var providers = databaseOperation.Repositories.ImageProviders;
        var stored = await providers.GetByIdAsync(providerId, cancellationToken)
            ?? throw new InvalidOperationException($"Image provider {providerId} was not found.");
        var now = DateTime.UtcNow;
        var nextSortOrder = stored.Models.Count == 0 ? 0 : stored.Models.Max(model => model.SortOrder) + 1;
        var changed = 0;
        foreach (var candidate in discovered)
        {
            var row = ToModel(candidate);
            NormalizeAndValidateModel(row);
            var existing = stored.Models.FirstOrDefault(model => model.ModelId == row.ModelId);
            if (existing is null)
            {
                row.ImageProviderId = stored.Id;
                row.SortOrder = nextSortOrder++;
                row.CreatedAt = now;
                row.UpdatedAt = now;
                await providers.AddModelAsync(row, cancellationToken);
                changed++;
            }
            else if (existing.Origin != ImageProviderModelOrigin.Manual)
            {
                // Listed capabilities replace the stored ones; manual rows stay as the user entered them.
                row.DisplayName = existing.DisplayName ?? row.DisplayName;
                CopyModel(row, existing);
                existing.UpdatedAt = now;
                changed++;
            }
        }

        await databaseOperation.SaveChangesAsync(cancellationToken);
        var refreshed = (await providers.GetByIdAsync(providerId, cancellationToken))!;
        if (refreshed.Models.Count > 0 && !refreshed.Models.Any(model => model.IsDefault))
        {
            await providers.SetDefaultModelAsync(providerId, refreshed.Models.First().Id, cancellationToken);
            await databaseOperation.SaveChangesAsync(cancellationToken);
        }
        return changed;
    }

    public async Task<ResolvedImageModel> ResolveAsync(int? providerId = null, string? modelId = null, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var providers = await databaseOperation.Repositories.ImageProviders.GetAllAsync(cancellationToken);
        var provider = providerId is { } id
            ? providers.FirstOrDefault(candidate => candidate.Id == id)
                ?? throw new ImageProviderNotConfiguredException("The selected image provider no longer exists. Choose another in Settings > Image providers.")
            : providers.FirstOrDefault(candidate => candidate.IsDefault)
                ?? throw new ImageProviderNotConfiguredException();
        var (ready, message) = await CheckReadyAsync(databaseOperation, provider, cancellationToken);
        if (!ready)
            throw new ImageProviderNotConfiguredException(message);

        var requestedModel = string.IsNullOrWhiteSpace(modelId) ? null : modelId.Trim();
        var model = requestedModel is null
            ? provider.Models.FirstOrDefault(candidate => candidate.IsDefault) ?? provider.Models.FirstOrDefault()
            : provider.Models.FirstOrDefault(candidate => candidate.ModelId == requestedModel)
                ?? throw new ArgumentException(
                    $"Image model '{requestedModel}' is not offered by {Label(provider)}. Use one of: {string.Join(", ", provider.Models.Select(candidate => candidate.ModelId))}.",
                    nameof(modelId));
        if (model is null)
            throw new ImageProviderNotConfiguredException($"{Label(provider)} has no image models. Refresh or add models in Settings > Image providers.");
        return ResolvedImageModel.From(provider, model);
    }

    public async Task<IReadOnlyList<ResolvedImageModel>> ListReadyModelsAsync(CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var providers = await databaseOperation.Repositories.ImageProviders.GetAllAsync(cancellationToken);
        var result = new List<ResolvedImageModel>();
        foreach (var provider in providers)
        {
            if (!(await CheckReadyAsync(databaseOperation, provider, cancellationToken)).Ready)
                continue;
            foreach (var model in provider.Models.OrderByDescending(model => model.IsDefault).ThenBy(model => model.SortOrder))
            {
                try
                {
                    result.Add(ResolvedImageModel.From(provider, model));
                }
                catch (ArgumentException)
                {
                    // A model whose stored raster limits are unusable is not offered until it is fixed.
                }
            }
        }
        return result;
    }

    public async Task<ImageProviderReadiness> GetReadinessAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var resolved = await ResolveAsync(cancellationToken: cancellationToken);
            return new ImageProviderReadiness(true, $"Images use {resolved.ModelLabel} on {resolved.ProviderLabel}.", resolved);
        }
        catch (ImageProviderNotConfiguredException ex)
        {
            return new ImageProviderReadiness(false, ex.Message, null);
        }
        catch (ArgumentException ex)
        {
            return new ImageProviderReadiness(false, ex.Message, null);
        }
    }

    public async Task<string> GetToolGuidanceAsync(CancellationToken cancellationToken = default)
    {
        var readiness = await GetReadinessAsync(cancellationToken);
        if (readiness.Default is not { } defaultModel)
            return " " + readiness.Message + " Image generation and editing fail until it is fixed.";

        var models = (await ListReadyModelsAsync(cancellationToken))
            .Where(model => model.ProviderId == defaultModel.ProviderId)
            .OrderByDescending(model => model.ModelId == defaultModel.ModelId)
            .ToList();
        return ProjectImageModelCatalog.BuildToolGuidance(models);
    }

    public static ImageProviderModel ToModel(DiscoveredImageModel discovered) => new()
    {
        ModelId = discovered.ModelId,
        DisplayName = discovered.DisplayName,
        Origin = discovered.Bundled ? ImageProviderModelOrigin.Bundled : ImageProviderModelOrigin.Discovered,
        QualitiesJson = ImageModelCapabilities.ToJson(discovered.Capabilities.Qualities),
        OutputFormatsJson = ImageModelCapabilities.ToJson(discovered.Capabilities.OutputFormats),
        SupportsGenerate = discovered.Capabilities.Generate,
        SupportsEdit = discovered.Capabilities.Edit,
        SupportsRegionalGuide = discovered.Capabilities.RegionalGuide,
        MaxReferenceImages = discovered.Capabilities.MaxReferenceImages,
        SupportsTransparentBackground = discovered.Capabilities.TransparentBackground,
        SupportsStreaming = discovered.Capabilities.Streaming,
        RasterConstraintsJson = discovered.Constraints.ToJson(),
    };

    private static async Task<(bool Ready, string Message)> CheckReadyAsync(
        AppDatabaseReadOperation databaseOperation,
        ImageProvider provider,
        CancellationToken cancellationToken)
    {
        var label = Label(provider);
        if (provider.Models.Count == 0)
            return (false, $"{label} has no image models. Refresh or add models in Settings > Image providers.");

        switch (provider.Kind)
        {
            case ImageProviderKind.ChatGptAccount:
                if (provider.OpenAiAccountId is not { } accountId)
                    return (false, $"{label} has no ChatGPT account. Choose one in Settings > Image providers.");
                var account = await databaseOperation.Repositories.OpenAiAccounts.GetByIdAsync(accountId, cancellationToken);
                if (account is null || account.Kind != OpenAiAccountKind.Codex)
                    return (false, $"{label} needs a ChatGPT (Codex) account. Choose one in Settings > Image providers.");
                if (account.RequiresReauthenticationAt is not null
                    || await databaseOperation.Repositories.OAuthTokens.GetLatestForAccountAsync(accountId, cancellationToken) is null)
                    return (false, "Connect or reauthenticate the ChatGPT (Codex) account in Settings > Providers before generating images.");
                return (true, string.Empty);
            case ImageProviderKind.OpenRouter:
                return string.IsNullOrWhiteSpace(provider.ApiKey)
                    ? (false, $"{label} needs an OpenRouter API key in Settings > Image providers.")
                    : (true, string.Empty);
            default:
                return string.IsNullOrWhiteSpace(provider.EndpointUrl)
                    ? (false, $"{label} needs an endpoint URL in Settings > Image providers.")
                    : (true, string.Empty);
        }
    }

    private static async Task ValidateAccountAsync(
        AppDatabaseReadOperation databaseOperation,
        ImageProvider provider,
        CancellationToken cancellationToken)
    {
        if (provider.Kind != ImageProviderKind.ChatGptAccount)
            return;
        var account = await databaseOperation.Repositories.OpenAiAccounts.GetByIdAsync(provider.OpenAiAccountId!.Value, cancellationToken);
        if (account is null)
            throw new ArgumentException("The selected ChatGPT account no longer exists.", nameof(provider));
        if (account.Kind != OpenAiAccountKind.Codex)
            throw new ArgumentException("ChatGPT plan sign-in cannot generate images. Choose the ChatGPT (Codex) account.", nameof(provider));
    }

    private void Validate(ImageProvider provider)
    {
        if (provider.Name.Length == 0 || !SlugPattern().IsMatch(provider.Name))
            throw new ArgumentException("Provider name must be a lowercase slug of letters, digits, and hyphens.", nameof(provider));
        if (!provider.ConfigurationJson.TrimStart().StartsWith('{'))
            throw new ArgumentException("Configuration JSON must be a JSON object.", nameof(provider));
        _ = factory.Get(provider.Kind);

        switch (provider.Kind)
        {
            case ImageProviderKind.ChatGptAccount:
                if (provider.OpenAiAccountId is null)
                    throw new ArgumentException("Choose the ChatGPT (Codex) account that serves image requests.", nameof(provider));
                if (provider.EndpointUrl is not null || provider.ApiKey is not null)
                    throw new ArgumentException("A ChatGPT account provider uses the account sign-in, not an endpoint or API key.", nameof(provider));
                break;
            case ImageProviderKind.OpenRouter:
                if (provider.ApiKey is null)
                    throw new ArgumentException("An OpenRouter API key is required.", nameof(provider));
                break;
            case ImageProviderKind.OpenAiImagesCompatible:
                if (provider.EndpointUrl is null)
                    throw new ArgumentException("An endpoint URL is required.", nameof(provider));
                break;
        }

        if (provider.Kind != ImageProviderKind.ChatGptAccount)
        {
            if (provider.OpenAiAccountId is not null)
                throw new ArgumentException("Only a ChatGPT account provider uses an account.", nameof(provider));
            if (provider.EndpointUrl is not null)
                ValidateEndpoint(provider.EndpointUrl);
        }
    }

    /// <summary>Endpoints must be https, except plain http to this machine.</summary>
    public static void ValidateEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("https" or "http")
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("The endpoint must be an absolute https URL without credentials, query, or fragment.", nameof(endpoint));
        if (uri.Scheme == "http" && !uri.IsLoopback)
            throw new ArgumentException("Plain http is allowed only for endpoints on this machine (localhost or 127.0.0.1). Use https.", nameof(endpoint));
    }

    private static void Normalize(ImageProvider provider)
    {
        provider.Name = (provider.Name ?? string.Empty).Trim().ToLowerInvariant();
        provider.DisplayName = Clean(provider.DisplayName);
        provider.EndpointUrl = Clean(provider.EndpointUrl)?.TrimEnd('/');
        provider.ApiKey = Clean(provider.ApiKey);
        provider.LastTestMessage = Clean(provider.LastTestMessage);
        provider.ConfigurationJson = string.IsNullOrWhiteSpace(provider.ConfigurationJson) ? "{}" : provider.ConfigurationJson.Trim();
    }

    private static void NormalizeAndValidateModel(ImageProviderModel model)
    {
        model.ModelId = (model.ModelId ?? string.Empty).Trim();
        model.DisplayName = Clean(model.DisplayName);
        if (model.ModelId.Length == 0)
            throw new ArgumentException("Model ID is required.", nameof(model));
        var capabilities = ImageModelCapabilities.From(model);
        model.QualitiesJson = ImageModelCapabilities.ToJson(capabilities.Qualities);
        model.OutputFormatsJson = ImageModelCapabilities.ToJson(capabilities.OutputFormats);
        if (capabilities.OutputFormats.Any(format => format is not ("png" or "jpeg" or "webp")))
            throw new ArgumentException("Output formats must be png, jpeg, or webp.", nameof(model));
        if (model.MaxReferenceImages is < 0 or > 64)
            throw new ArgumentException("Reference image limit must be between 0 and 64.", nameof(model));
        if (!model.SupportsGenerate && !model.SupportsEdit)
            throw new ArgumentException("A model must generate or edit images.", nameof(model));
        if (model.SupportsRegionalGuide && !model.SupportsEdit)
            throw new ArgumentException("Regional guides require edit support.", nameof(model));
        model.RasterConstraintsJson = ImageRasterConstraints.FromJson(model.RasterConstraintsJson).ToJson();
    }

    private static void CopyModel(ImageProviderModel source, ImageProviderModel target)
    {
        target.ModelId = source.ModelId;
        target.DisplayName = source.DisplayName;
        target.Origin = source.Origin;
        target.QualitiesJson = source.QualitiesJson;
        target.OutputFormatsJson = source.OutputFormatsJson;
        target.SupportsGenerate = source.SupportsGenerate;
        target.SupportsEdit = source.SupportsEdit;
        target.SupportsRegionalGuide = source.SupportsRegionalGuide;
        target.MaxReferenceImages = source.MaxReferenceImages;
        target.SupportsTransparentBackground = source.SupportsTransparentBackground;
        target.SupportsStreaming = source.SupportsStreaming;
        target.RasterConstraintsJson = source.RasterConstraintsJson;
    }

    private static string Label(ImageProvider provider) => provider.DisplayName ?? provider.Name;

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$")]
    private static partial Regex SlugPattern();
}
