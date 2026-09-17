using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace Lorekeeper.Authorization;

public sealed record OpenAiCallbackOriginValidation(
    bool IsValid,
    Uri? RedirectUri,
    string Message);

public interface IOpenAiCallbackOriginValidator
{
    OpenAiCallbackOriginValidation Validate();
}

public sealed class OpenAiCallbackOriginValidator(
    IConfiguration configuration,
    IServer server) : IOpenAiCallbackOriginValidator
{
    private const string InvalidConfigurationMessage =
        "OpenAI connection is unavailable because Auth:Codex:RedirectUri must be an HTTP loopback URL ending in /auth/callback.";
    private const string InactiveOriginMessage =
        "OpenAI connection is unavailable because the configured callback port is not served by this Lorekeeper host. Update Auth:Codex:RedirectUri or start Lorekeeper on that port.";

    public OpenAiCallbackOriginValidation Validate()
    {
        var configured = configuration["Auth:Codex:RedirectUri"];
        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses ?? [];
        return Validate(configured, addresses);
    }

    public static OpenAiCallbackOriginValidation Validate(
        string? configuredRedirectUri,
        IEnumerable<string> activeAddresses)
    {
        if (!Uri.TryCreate(configuredRedirectUri, UriKind.Absolute, out var redirectUri)
            || redirectUri.Scheme != Uri.UriSchemeHttp
            || !redirectUri.IsLoopback
            || !string.IsNullOrEmpty(redirectUri.UserInfo)
            || !string.IsNullOrEmpty(redirectUri.Query)
            || !string.IsNullOrEmpty(redirectUri.Fragment)
            || !redirectUri.AbsolutePath.Equals("/auth/callback", StringComparison.Ordinal))
        {
            return new OpenAiCallbackOriginValidation(false, null, InvalidConfigurationMessage);
        }

        var configuredOrigin = redirectUri.GetLeftPart(UriPartial.Authority);
        var originIsActive = activeAddresses
            .Select(address => Uri.TryCreate(address, UriKind.Absolute, out var uri) ? uri : null)
            .Any(address => address is not null
                && address.GetLeftPart(UriPartial.Authority).Equals(configuredOrigin, StringComparison.OrdinalIgnoreCase));
        return originIsActive
            ? new OpenAiCallbackOriginValidation(true, redirectUri, string.Empty)
            : new OpenAiCallbackOriginValidation(false, redirectUri, InactiveOriginMessage);
    }
}
