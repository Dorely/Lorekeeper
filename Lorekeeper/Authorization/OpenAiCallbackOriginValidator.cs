using Lorekeeper.Models;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace Lorekeeper.Authorization;

public sealed record OpenAiCallbackOriginValidation(
    bool IsValid,
    Uri? RedirectUri,
    string Message);

public interface IOpenAiCallbackOriginValidator
{
    OpenAiCallbackOriginValidation Validate(OpenAiAccountKind kind);
}

/// <summary>
/// Resolves the loopback redirect for an account kind. Codex uses the configured
/// <c>Auth:Codex:RedirectUri</c>; ChatGPT plan sign-in requires the same port and
/// path on <c>127.0.0.1</c>, which a <c>localhost</c> binding also serves.
/// </summary>
public sealed class OpenAiCallbackOriginValidator(
    IConfiguration configuration,
    IServer server) : IOpenAiCallbackOriginValidator
{
    private const string InvalidConfigurationMessage =
        "OpenAI connection is unavailable because Auth:Codex:RedirectUri must be an HTTP loopback URL ending in /auth/callback.";
    private const string InactiveOriginMessage =
        "OpenAI connection is unavailable because the configured callback port is not served by this Lorekeeper host. Update Auth:Codex:RedirectUri or start Lorekeeper on that port.";

    public OpenAiCallbackOriginValidation Validate(OpenAiAccountKind kind)
    {
        var configured = configuration["Auth:Codex:RedirectUri"];
        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses ?? [];
        return Validate(kind, configured, addresses);
    }

    public static OpenAiCallbackOriginValidation Validate(
        OpenAiAccountKind kind,
        string? configuredRedirectUri,
        IEnumerable<string> activeAddresses)
    {
        if (!Uri.TryCreate(configuredRedirectUri, UriKind.Absolute, out var configured)
            || configured.Scheme != Uri.UriSchemeHttp
            || !configured.IsLoopback
            || !string.IsNullOrEmpty(configured.UserInfo)
            || !string.IsNullOrEmpty(configured.Query)
            || !string.IsNullOrEmpty(configured.Fragment)
            || !configured.AbsolutePath.Equals("/auth/callback", StringComparison.Ordinal))
        {
            return new OpenAiCallbackOriginValidation(false, null, InvalidConfigurationMessage);
        }

        var redirectUri = kind == OpenAiAccountKind.ChatGptPlan
            ? new UriBuilder(configured) { Host = "127.0.0.1" }.Uri
            : configured;
        var active = activeAddresses
            .Select(address => Uri.TryCreate(address, UriKind.Absolute, out var uri) ? uri : null)
            .OfType<Uri>()
            .ToList();
        var originIsActive = active.Any(address => SameOrigin(address, redirectUri))
            || (kind == OpenAiAccountKind.ChatGptPlan
                && active.Any(address => address.Scheme == Uri.UriSchemeHttp
                    && address.Port == redirectUri.Port
                    && address.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)));
        return originIsActive
            ? new OpenAiCallbackOriginValidation(true, redirectUri, string.Empty)
            : new OpenAiCallbackOriginValidation(false, redirectUri, InactiveOriginMessage);
    }

    private static bool SameOrigin(Uri left, Uri right) =>
        left.GetLeftPart(UriPartial.Authority).Equals(right.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);
}
